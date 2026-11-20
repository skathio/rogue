using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Wires the messaging feature module into the generator. When the SkathIO.Rogue.Messaging package is
/// referenced it emits, into its own file, one message binding per type marked <c>[Message("name")]</c>:
/// the stable name and a typed dispatcher that publishes an event through <c>IPublisher</c> or sends a
/// command through <c>ISender</c>. An inbound message is looked up by that name, never by a type name.
/// The only change to the dispatcher is that its <c>Publish</c> hands events to the bus after their
/// local handlers (see <c>DispatcherEmitter.EmitPublishOverride</c>).
/// </summary>
internal static class MessagingModule
{
    internal const string HintName = "RogueMessaging.g.cs";

    private static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    internal static IncrementalValueProvider<bool> RuntimeAvailable(IncrementalGeneratorInitializationContext context)
        => context.CompilationProvider.Select(static (compilation, _) => compilation.GetTypeByMetadataName(WellKnownTypeNames.MessageBinding1) is not null);

    internal static void Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<MessageTypeModel>> types =
            FeatureModules.MarkedTypes(context, WellKnownTypeNames.MessageAttribute, static (ctx, ct) => Extract(ctx, ct));

        context.RegisterSourceOutput(types.Combine(RuntimeAvailable(context)), static (spc, pair) =>
        {
            if (!pair.Right || pair.Left.IsEmpty)
            {
                return;
            }
            spc.AddSource(HintName, SourceText.From(CodeWriter.Header + Emit(pair.Left), Encoding.UTF8));
        });
    }

    // A generic type or one generated code cannot name (a private nested type) gets no binding, as a
    // command with such a request gets no job definition.
    private static MessageTypeModel? Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || type.IsGenericType || !CanName(type))
        {
            return null;
        }
        ct.ThrowIfCancellationRequested();

        string? name = null;
        foreach (AttributeData attr in ctx.Attributes)
        {
            if (attr.ConstructorArguments.Length == 1)
            {
                name = attr.ConstructorArguments[0].Value as string;
            }
        }

        // ICommand extends ICommand<Unit>, so a void command matches both and is sent as a void command.
        bool isEvent = false;
        bool isVoidCommand = false;
        string? response = null;
        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            string metadataName = iface.OriginalDefinition.ContainingNamespace.ToDisplayString() + "." + iface.OriginalDefinition.MetadataName;
            isEvent |= metadataName == WellKnownTypeNames.IEvent;
            isVoidCommand |= metadataName == WellKnownTypeNames.ICommand;
            if (metadataName == WellKnownTypeNames.ICommand1 && CanName(iface.TypeArguments[0]))
            {
                response = iface.TypeArguments[0].ToDisplayString(_typeFormat);
            }
        }
        MessageShape shape = isEvent ? MessageShape.Event
            : isVoidCommand ? MessageShape.Command
            : response is not null ? MessageShape.CommandWithResponse
            : MessageShape.PublishOnly;

        return new MessageTypeModel(type.ToDisplayString(_typeFormat), name, shape, response);
    }

    // Whether code in the type's own compilation, outside the type, can name it. Type arguments of a
    // generic response type are not checked; a response the generated code cannot name fails the build.
    private static bool CanName(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol)
        {
            return false;
        }
        for (INamedTypeSymbol? current = (INamedTypeSymbol)type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }
        return true;
    }

    private static string Emit(ImmutableArray<MessageTypeModel> types)
    {
        var w = new CodeWriter();
        FeatureModules.OpenRegistrar(w, "RogueMessagingRegistration");
        var seen = new HashSet<string>();
        foreach (MessageTypeModel type in types)
        {
            if (seen.Add(type.TypeFqn))
            {
                EmitBinding(w, type);
            }
        }
        FeatureModules.CloseRegistrar(w);
        return w.ToString();
    }

    private static void EmitBinding(CodeWriter w, MessageTypeModel type)
    {
        const string resolve = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService";
        string dispatch = type.Shape switch
        {
            MessageShape.Event => "static (services, message, cancellationToken) => " + resolve + "<global::SkathIO.Rogue.IPublisher>(services).Publish((global::SkathIO.Rogue.IEvent)message, cancellationToken)",
            MessageShape.Command => "static (services, message, cancellationToken) => " + resolve + "<global::SkathIO.Rogue.ISender>(services).Send((global::SkathIO.Rogue.ICommand)message, cancellationToken)",
            MessageShape.CommandWithResponse => "static (services, message, cancellationToken) => global::SkathIO.Rogue.Messaging.MessageBinding.IgnoreResult("
                + resolve + "<global::SkathIO.Rogue.ISender>(services).Send<" + type.ResponseFqn + ">((global::SkathIO.Rogue.ICommand<" + type.ResponseFqn + ">)message, cancellationToken))",
            _ => "null",
        };

        // A null name reaches the binding's constructor, which fails the host at startup naming the type.
        string name = type.Name is null ? "null!" : SymbolDisplay.FormatLiteral(type.Name, quote: true);
        w.Line("global::SkathIO.Rogue.Messaging.MessagingServiceCollectionExtensions.AddMessageBinding(services, new global::SkathIO.Rogue.Messaging.MessageBinding<" + type.TypeFqn + ">(");
        w.Line("    " + name + ",");
        w.Line("    " + dispatch + "));");
    }
}
