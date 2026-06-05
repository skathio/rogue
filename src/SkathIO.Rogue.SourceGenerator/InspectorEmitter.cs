using System.Collections.Generic;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Emits the generated <c>RoguePipelineInspector</c> class in <c>SkathIO.Rogue.Generated</c> namespace.
/// Output: <c>RoguePipelineInspector.g.cs</c>.
/// </summary>
internal static class InspectorEmitter
{
    internal static string Emit(DiscoveredModels models, RogueEmitOptions opts)
    {
        var w = new CodeWriter();

        w.Line("namespace SkathIO.Rogue.Generated");
        w.Line("{");
        w.Indent();

        w.Open("internal sealed class RoguePipelineInspector : global::SkathIO.Rogue.IRoguePipelineInspector");

        // Build a static readonly dictionary of Type → BehaviorInfo[]
        // Keyed by the request type's FQN (discovered at generator time).
        // This is O(1) per lookup after the first call (FrozenDictionary in the future; Dictionary for Phase 4.1).

        // Compute the pipeline info for each handler at generator time
        var pipelinesByRequest = BuildPipelinesByRequest(models);

        // ── Static lookup table ───────────────────────────────────────────────────────
        const string DictType = "global::System.Collections.Generic.Dictionary<global::System.Type, global::SkathIO.Rogue.BehaviorInfo[]>";

        if (pipelinesByRequest.Count > 0)
        {
            w.Line("private static readonly " + DictType + " s_pipelines");
            w.Indent();
            w.Line("= new " + DictType);
            w.Line("{");
            w.Indent();

            foreach (var kvp in pipelinesByRequest)
            {
                string requestFqn = DispatcherEmitter.ToGlobalFqn(kvp.Key);
                w.Line("{ typeof(" + requestFqn + "),");
                w.Indent();
                w.Line("new global::SkathIO.Rogue.BehaviorInfo[]");
                w.Line("{");
                w.Indent();
                int order = 0;
                foreach (var bEntry in kvp.Value)
                {
                    string bTypeFqn = DispatcherEmitter.ToGlobalFqn(bEntry.TypeFqn);
                    w.Line("new global::SkathIO.Rogue.BehaviorInfo(typeof(" + bTypeFqn + "), " + order + ", \"" + bEntry.Source + "\"),");
                    order++;
                }
                w.Dedent();
                w.Line("} },");
                w.Dedent();
            }

            w.Dedent();
            w.Line("};");
            w.Dedent();
        }
        else
        {
            w.Line("private static readonly " + DictType + " s_pipelines");
            w.Indent();
            w.Line("= new " + DictType + "();");
            w.Dedent();
        }

        w.Line();

        // ── GetPipeline<TRequest>() ───────────────────────────────────────────────────
        w.Open("public global::System.Collections.Generic.IReadOnlyList<global::SkathIO.Rogue.BehaviorInfo> GetPipeline<TRequest>() where TRequest : global::SkathIO.Rogue.IBaseRequest");
        w.Line("return GetPipeline(typeof(TRequest));");
        w.Close();
        w.Line();

        // ── GetPipeline(Type) ─────────────────────────────────────────────────────────
        w.Open("public global::System.Collections.Generic.IReadOnlyList<global::SkathIO.Rogue.BehaviorInfo> GetPipeline(global::System.Type requestType)");
        w.Open("if (s_pipelines.TryGetValue(requestType, out var pipeline))");
        w.Line("return pipeline;");
        w.Close(); // if
        w.Line("return global::System.Array.Empty<global::SkathIO.Rogue.BehaviorInfo>();");
        w.Close(); // GetPipeline(Type)

        w.Close(); // class RoguePipelineInspector

        w.Dedent();
        w.Line("}"); // namespace

        return w.ToString();
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Build pipeline metadata per request type
    // ────────────────────────────────────────────────────────────────────────────────────

    private sealed class BehaviorEntry
    {
        internal string TypeFqn { get; }
        internal string Source  { get; }

        internal BehaviorEntry(string typeFqn, string source)
        {
            TypeFqn = typeFqn;
            Source  = source;
        }
    }

    private static Dictionary<string, List<BehaviorEntry>> BuildPipelinesByRequest(DiscoveredModels models)
    {
        var result = new Dictionary<string, List<BehaviorEntry>>();

        // Apply the same PD-13a ordering used by RegistrationEmitter so the inspector reports the
        // pipeline in execution order (lower [BehaviorOrder] = outermost), then source-before-
        // metadata, then FQN lexicographic. The inspector covers only non-stream behaviors (its
        // GetPipeline is keyed on IBaseRequest, which stream requests do not implement).
        var orderedBehaviors = new List<BehaviorModel>(models.Behaviors.Count);
        orderedBehaviors.AddRange(models.Behaviors);
        orderedBehaviors.Sort(RegistrationEmitter.CompareBehaviorOrder);

        foreach (var handler in models.Handlers)
        {
            bool isVoid       = handler.ResponseFqn is null;
            string requestFqn = handler.RequestFqn;
            string responseFqn = isVoid ? "SkathIO.Rogue.Unit" : handler.ResponseFqn!;

            var entries = new List<BehaviorEntry>();

            foreach (var behavior in orderedBehaviors)
            {
                if (behavior.IsAbstract || !behavior.HasPublicCtor) continue;
                if (behavior.IsStream) continue;

                if (behavior.IsOpen)
                {
                    // Closed form of this open behavior for this request/response
                    string closedTypeFqn = behavior.UnboundTypeFqn + "<" + requestFqn + ", " + responseFqn + ">";
                    entries.Add(new BehaviorEntry(closedTypeFqn, "open-generic"));
                }
                else
                {
                    if (behavior.ClosedRequestFqn != requestFqn || behavior.ClosedResponseFqn != responseFqn)
                        continue;
                    entries.Add(new BehaviorEntry(behavior.TypeFqn, "closed"));
                }
            }

            if (entries.Count > 0)
                result[requestFqn] = entries;
        }

        return result;
    }
}
