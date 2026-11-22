namespace SkathIO.Rogue.SourceGenerator;

/// <summary>How a validated property's value is walked after its own rules.</summary>
internal enum NestedKind { None, Object, Collection }

/// <summary>
/// How a collection of <c>[Validate]</c> items is iterated. Interface-typed lists are indexed so no
/// enumerator is allocated; every other type uses <c>foreach</c>, whose pattern-based enumerator is a
/// struct for arrays, <c>List&lt;T&gt;</c> and the immutable collections.
/// </summary>
internal enum CollectionShape { Foreach, ReadOnlyList, List }

/// <summary>One rule attribute on a property: the rule type and its arguments as C# literals.</summary>
internal sealed record RuleModel(
    string RuleFqn,
    EquatableArray<string> Arguments,
    /// <summary>The <c>Code</c> named argument as a C# literal, or null to use the rule's <c>DefaultCode</c>.</summary>
    string? CodeLiteral,
    /// <summary>The <c>Message</c> named argument as a C# literal, or null to use the rule's <c>DefaultMessage</c>.</summary>
    string? MessageLiteral);

/// <summary>A property the generated validator checks.</summary>
internal sealed record ValidatedPropertyModel(
    string Name,
    /// <summary>The property name as a C# identifier (escaped with <c>@</c> when it is a keyword).</summary>
    string Identifier,
    /// <summary>True when the value can be null (a reference type or <c>Nullable&lt;T&gt;</c>).</summary>
    bool CanBeNull,
    /// <summary>True for a non-nullable reference type in a nullable-enabled context: null is an error.</summary>
    bool ImpliedNotNull,
    EquatableArray<RuleModel> Rules,
    NestedKind Nested,
    /// <summary>The nested <c>[Validate]</c> type, or the collection's item type, as a global C# type name.</summary>
    string? NestedTypeFqn,
    CollectionShape Shape,
    bool ItemCanBeNull,
    bool ItemImpliedNotNull);

/// <summary>A declaration that encloses the validator: a containing type, or the validated type itself.</summary>
internal sealed record TypeDeclarationModel(
    /// <summary>The declaration keywords, such as <c>record</c>, <c>readonly record struct</c> or <c>class</c>.</summary>
    string Keyword,
    /// <summary>The type name with its type parameter list, such as <c>Page&lt;T&gt;</c>.</summary>
    string Name);

/// <summary>A diagnostic found while reading the validation model, reported by the validation output.</summary>
internal sealed record ValidationDiagnosticModel(string Id, EquatableArray<string> Arguments);

/// <summary>A type marked <c>[Validate]</c> in this compilation.</summary>
internal sealed record ValidatedTypeModel(
    /// <summary>The type as a global C# type name, such as <c>global::App.CreateOrder</c>.</summary>
    string TypeFqn,
    /// <summary>The containing namespace, or null for the global namespace.</summary>
    string? Namespace,
    /// <summary>The containing types from outermost in, then the validated type itself.</summary>
    EquatableArray<TypeDeclarationModel> Declarations,
    /// <summary>False when the type or a containing type is not partial: no validator can be emitted.</summary>
    bool CanEmit,
    bool IsReferenceType,
    bool HasRulesHook,
    EquatableArray<ValidatedPropertyModel> Properties,
    EquatableArray<ValidationDiagnosticModel> Diagnostics);

/// <summary>A class implementing <c>IAsyncRules&lt;T&gt;</c> in this compilation.</summary>
internal sealed record AsyncRulesModel(
    string TypeFqn,
    string RequestFqn,
    /// <summary>True when the class can be registered: concrete, not generic, with a public constructor.</summary>
    bool CanRegister,
    /// <summary>True when the checked type carries <c>[Validate]</c>; otherwise the rules never run.</summary>
    bool RequestIsValidated);

/// <summary>Everything the validation output needs, gathered from the compilation.</summary>
internal sealed record ValidationInputModel(
    EquatableArray<ValidatedTypeModel> Types,
    EquatableArray<AsyncRulesModel> AsyncRules,
    /// <summary>Types that declare rule attributes but carry no <c>[Validate]</c> (ROGUE018).</summary>
    EquatableArray<string> UnmarkedRuleOwners,
    /// <summary>
    /// Request types marked <c>[Validate]</c> in a referenced assembly that carries no generated
    /// validator for them (ROGUE017).
    /// </summary>
    EquatableArray<string> RequestsWithoutValidator);
