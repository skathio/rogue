using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The CI benchmark smoke for the jobs and validation benches: .github/workflows/ci.yml runs each bench
/// project, like the NoBehavior "Benchmark smoke" job, and fails when the allocations it measures go
/// over limits stored in .bench/thresholds.json. The limits agree with the committed measurements under
/// bench/results/&lt;date&gt;-&lt;sha&gt;/. Each test runs the job's own shell steps, from the bench run on,
/// in a scratch copy of the repository where `dotnet run` writes a BenchmarkDotNet JSON result built
/// from the newest committed CSV report of each benchmark class the run selects.
/// </summary>
public sealed class BenchSmokeRulesTests
{
    private const string JobsBench = "bench/SkathIO.Rogue.Benchmarks.Jobs";
    private const string ValidationBench = "bench/SkathIO.Rogue.Benchmarks.Validation";
    private const string ThresholdsPath = ".bench/thresholds.json";
    private const long RegressionFloorBytes = 64 * 1024;
    private static readonly string[] _noBehaviorKeys = { "scenario1_rogue_allocated_bytes", "scenario1_rogue_ns_p95", "interface_path_ceiling_bytes" };

    public static TheoryData<string> BenchProjects() => new TheoryData<string>(JobsBench, ValidationBench);

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void Ci_HasASmokeJob_ThatRunsTheBench_AndReadsItsLimitsFromTheThresholds(string project)
    {
        SmokeJob job = SmokeJob.For(project);

        Assert.Contains(ThresholdsPath, job.Text, StringComparison.Ordinal);
        Assert.True(job.LimitKeys().Length > 0, "The CI job that runs " + project + " reads no limit of its own from " + ThresholdsPath + ".");
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void EveryLimitTheSmokeReads_IsANonNegativeNumber(string project)
    {
        JsonObject thresholds = Thresholds();
        string[] keys = SmokeJob.For(project).LimitKeys();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.All(Numbers(thresholds[key]!), n => Assert.True(
            n.GetValue<double>() >= 0, key + " in " + ThresholdsPath + " holds the negative limit " + n.ToJsonString() + ".")));
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void TheSmoke_Passes_OnTheCommittedMeasurements(string project)
    {
        SmokeOutcome outcome = SmokeRun.Execute(project, static bytes => bytes, Thresholds());

        Assert.True(outcome.Staged > 0, "The CI smoke for " + project + " selects no benchmark that has committed results.");
        Assert.True(outcome.ExitCode == 0, "The smoke fails on the committed measurements:\n" + outcome.Output);
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void TheSmoke_Fails_WhenEveryAllocationGrowsTenfold(string project)
    {
        SmokeOutcome outcome = SmokeRun.Execute(project, static bytes => Math.Max(bytes * 10, bytes + RegressionFloorBytes), Thresholds());

        Assert.True(outcome.Staged > 0, "The CI smoke for " + project + " selects no benchmark that has committed results.");
        Assert.True(outcome.ExitCode != 0, "The smoke passes with every allocation ten times the committed one:\n" + outcome.Output);
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void TheSmoke_Fails_OnTheCommittedMeasurements_WhenItsLimitsInTheThresholdsAreBelowZero(string project)
    {
        JsonObject thresholds = Thresholds();
        string[] keys = SmokeJob.For(project).LimitKeys();
        foreach (string key in keys)
        {
            thresholds[key] = BelowZero(thresholds[key]!);
        }

        SmokeOutcome outcome = SmokeRun.Execute(project, static bytes => bytes, thresholds);

        Assert.NotEmpty(keys);
        Assert.True(outcome.ExitCode != 0, "The smoke passes with " + string.Join(", ", keys) + " set below zero, so it does not gate on them:\n" + outcome.Output);
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void TheSmoke_Fails_WhenTheRunLeavesNoResult(string project)
    {
        SmokeOutcome outcome = SmokeRun.Execute(project, transform: null, Thresholds());

        Assert.True(outcome.ExitCode != 0, "The smoke passes with no benchmark result to read:\n" + outcome.Output);
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void TheSmoke_Fails_WhenTheResultHoldsNoBenchmark(string project)
    {
        SmokeOutcome outcome = SmokeRun.Execute(project, static _ => -1, Thresholds());

        Assert.True(outcome.ExitCode != 0, "The smoke passes on results that hold no benchmark:\n" + outcome.Output);
    }

    internal static string[] NoBehaviorKeys => _noBehaviorKeys;

    internal static JsonObject Thresholds() => JsonNode.Parse(RepoFiles.Read(ThresholdsPath))!.AsObject();

    private static IEnumerable<JsonValue> Numbers(JsonNode node) => node switch
    {
        JsonObject o => o.Select(static p => p.Value).Where(static v => v is not null).SelectMany(static v => Numbers(v!)),
        JsonArray a => a.Where(static v => v is not null).SelectMany(static v => Numbers(v!)),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => new[] { v },
        _ => Array.Empty<JsonValue>(),
    };

    private static JsonNode BelowZero(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(static p => KeyValuePair.Create(p.Key, p.Value is null ? null : BelowZero(p.Value)))),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(-1),
        _ => node.DeepClone(),
    };
}

/// <summary>A ci.yml job that runs a bench project: its text, its shell steps and its bench commands.</summary>
internal sealed class SmokeJob
{
    private SmokeJob(string project, string text)
    {
        Project = project;
        Text = text;
        Steps = RunSteps(text);
    }

    public string Project { get; }

    public string Text { get; }

    public IReadOnlyList<string> Steps { get; }

    public static SmokeJob For(string project)
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");
        string[] jobs = Regex.Split(ci, @"^(?=  [A-Za-z0-9_-]+:\s*$)", RegexOptions.Multiline);
        string[] running = jobs.Where(j => RunsBench(j, project)).ToArray();

        Assert.True(running.Length > 0, "No job in .github/workflows/ci.yml runs `dotnet run --project " + project + "`.");
        return new SmokeJob(project, string.Join("\n", running));
    }

    public static bool RunsBench(string text, string project) =>
        Regex.IsMatch(text, @"dotnet\s+run\b[^\n]*--project\s+[""']?(?:\./)?" + Regex.Escape(project) + @"(?:[/""'\s]|$)", RegexOptions.Multiline);

    /// <summary>The steps from the first one that runs this bench to the end of the job, the gate among them.</summary>
    public IReadOnlyList<string> StepsFromTheBenchRun() => Steps.SkipWhile(s => !RunsBench(s, Project)).ToArray();

    /// <summary>Each `dotnet run` of a bench project in the job, as its shell words.</summary>
    public IEnumerable<string[]> BenchCommands() =>
        Steps.SelectMany(static s => Regex.Replace(s, @"\\\r?\n", " ").Split('\n'))
            .Where(static line => Regex.IsMatch(line, @"dotnet\s+run\b"))
            .Select(ShellWords);

    /// <summary>The thresholds keys the job, or a script it calls, reads, past the NoBehavior smoke's own.</summary>
    public string[] LimitKeys()
    {
        JsonObject thresholds = BenchSmokeRulesTests.Thresholds();
        string text = Text + "\n" + string.Join("\n", ReferencedScripts());
        return Regex.Matches(text, @"\.(?<key>[A-Za-z_][A-Za-z0-9_]*)\b")
            .Select(static m => m.Groups["key"].Value)
            .Where(key => thresholds[key] is JsonObject || thresholds[key]?.GetValueKind() == JsonValueKind.Number)
            .Where(static key => !BenchSmokeRulesTests.NoBehaviorKeys.Contains(key, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private IEnumerable<string> ReferencedScripts() =>
        Regex.Matches(Text, @"(?<![\w/.-])(?:\./)?(?<path>[\w.-]+(?:/[\w.-]+)*\.(?:sh|bash|mjs|js|py|jq|ps1))\b")
            .Select(static m => Path.Combine(RepoFiles.Root, m.Groups["path"].Value))
            .Where(File.Exists)
            .Select(File.ReadAllText);

    private static string[] RunSteps(string job)
    {
        string[] lines = job.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var steps = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match run = Regex.Match(lines[i], @"^(?<indent>\s*)(?<dash>-\s+)?run:\s*(?<rest>.*?)\s*$");
            if (!run.Success)
            {
                continue;
            }
            int keyIndent = run.Groups["indent"].Length + run.Groups["dash"].Length;
            string rest = run.Groups["rest"].Value;
            if (!Regex.IsMatch(rest, @"^[|>][+-]?$"))
            {
                steps.Add(rest.Trim('\'', '"'));
                continue;
            }
            int end = BlockEnd(lines, i + 1, keyIndent);
            string[] block = lines[(i + 1)..end];
            int dedent = block.Where(static l => l.Trim().Length > 0).Select(Indent).DefaultIfEmpty(0).Min();
            steps.Add(string.Join("\n", block.Select(l => l.Length >= dedent ? l[dedent..] : string.Empty)));
            i = end - 1;
        }
        return steps.ToArray();
    }

    // The line after a YAML block scalar: the first non-blank line indented no deeper than its key.
    private static int BlockEnd(string[] lines, int start, int keyIndent)
    {
        int end = start;
        while (end < lines.Length && (lines[end].Trim().Length == 0 || Indent(lines[end]) > keyIndent))
        {
            end++;
        }
        return end;
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static string[] ShellWords(string line)
    {
        var words = new List<string>();
        foreach (Match m in Regex.Matches(line, @"'(?<w>[^']*)'|""(?<w>[^""]*)""|(?<w>[^\s'""]+)"))
        {
            words.Add(m.Groups["w"].Value);
        }
        return words.ToArray();
    }
}

/// <summary>What a smoke run did: how many benchmarks it was given, its exit code and its output.</summary>
internal sealed record SmokeOutcome(int Staged, int ExitCode, string Output);

/// <summary>The scratch folder of a smoke run, the workspace the steps run in, and the staged results.</summary>
internal sealed record SmokeDirs(string Scratch, string Workspace, string Staged);

/// <summary>
/// Runs a smoke job's steps in a scratch workspace that links to the repository, with its own copy of
/// .bench/. `dotnet` is a shell function there: `dotnet run` copies the staged BenchmarkDotNet results
/// into the run's artifacts folder, as a real run would leave them, and every other call does nothing.
/// </summary>
internal static class SmokeRun
{
    private const string DotnetShim = """
        dotnet() {
          if [ "${1:-}" = "run" ]; then
            local dir="BenchmarkDotNet.Artifacts" prev=""
            for arg in "$@"; do
              if [ "$prev" = "--artifacts" ]; then dir="$arg"; fi
              prev="$arg"
            done
            mkdir -p "$dir/results"
            cp -R "$ROGUE_SMOKE_STAGED/." "$dir/results/"
          fi
          return 0
        }
        export -f dotnet

        """;

    /// <summary>
    /// Stages the committed rows with their bytes passed through <paramref name="transform"/>; a negative
    /// result stages an empty result file, and a null transform stages no file at all.
    /// </summary>
    public static SmokeOutcome Execute(string project, Func<long, long>? transform, JsonObject thresholds)
    {
        SmokeJob job = SmokeJob.For(project);
        string scratch = Directory.CreateTempSubdirectory("rogue-bench-smoke-").FullName;
        try
        {
            string workspace = Workspace(scratch, thresholds);
            string staged = Directory.CreateDirectory(Path.Combine(scratch, "staged")).FullName;
            int count = transform is null ? 0 : BenchResults.Stage(job, transform, staged);
            (int exit, string output) = RunSteps(job, new SmokeDirs(scratch, workspace, staged));
            return new SmokeOutcome(count, exit, output);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string Workspace(string scratch, JsonObject thresholds)
    {
        string workspace = Directory.CreateDirectory(Path.Combine(scratch, "repo")).FullName;
        string[] own = { ".git", ".bench", "BenchmarkDotNet.Artifacts" };
        foreach (string entry in Directory.GetFileSystemEntries(RepoFiles.Root).Where(e => !own.Contains(Path.GetFileName(e), StringComparer.Ordinal)))
        {
            string link = Path.Combine(workspace, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.CreateSymbolicLink(link, entry);
            }
            else
            {
                File.CreateSymbolicLink(link, entry);
            }
        }
        string bench = Directory.CreateDirectory(Path.Combine(workspace, ".bench")).FullName;
        foreach (string file in Directory.GetFiles(Path.Combine(RepoFiles.Root, ".bench")))
        {
            File.Copy(file, Path.Combine(bench, Path.GetFileName(file)));
        }
        File.WriteAllText(Path.Combine(bench, "thresholds.json"), thresholds.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return workspace;
    }

    private static (int ExitCode, string Output) RunSteps(SmokeJob job, SmokeDirs dirs)
    {
        var output = new StringBuilder();
        IReadOnlyList<string> steps = job.StepsFromTheBenchRun();
        Assert.NotEmpty(steps);
        for (int i = 0; i < steps.Count; i++)
        {
            string script = Path.Combine(dirs.Scratch, "step-" + i.ToString(CultureInfo.InvariantCulture) + ".sh");
            File.WriteAllText(script, DotnetShim + steps[i] + "\n");
            int exit = Bash(script, dirs, output);
            if (exit != 0)
            {
                return (exit, output.ToString());
            }
        }
        return (0, output.ToString());
    }

    private static int Bash(string script, SmokeDirs dirs, StringBuilder output)
    {
        var start = new ProcessStartInfo("bash") { WorkingDirectory = dirs.Workspace, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--noprofile", "--norc", "-eo", "pipefail", script })
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["ROGUE_SMOKE_STAGED"] = dirs.Staged;
        start.Environment["GITHUB_WORKSPACE"] = dirs.Workspace;
        start.Environment["RUNNER_TEMP"] = dirs.Scratch;
        start.Environment["CI"] = "true";
        foreach (string file in new[] { "GITHUB_OUTPUT", "GITHUB_ENV", "GITHUB_STEP_SUMMARY", "GITHUB_PATH" })
        {
            start.Environment[file] = Path.Combine(dirs.Scratch, file.ToLowerInvariant());
        }
        using Process process = Process.Start(start)!;
        System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();
        output.Append(process.StandardOutput.ReadToEnd());
        bool exited = process.WaitForExit(TimeSpan.FromMinutes(2));
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }
        Assert.True(exited, "A smoke step did not finish:\n" + output);
        output.Append(stderr.Result);
        return process.ExitCode;
    }
}

/// <summary>Builds BenchmarkDotNet JSON results from the newest committed CSV report of each class.</summary>
internal static class BenchResults
{
    private static readonly Dictionary<string, string> _jsonSuffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["json"] = "-report-full-compressed.json",
        ["fulljson"] = "-report-full.json",
        ["briefjson"] = "-report-brief.json",
    };

    /// <summary>Writes a result file for each class each bench run of the job selects; returns the rows written.</summary>
    public static int Stage(SmokeJob job, Func<long, long> transform, string staged)
    {
        int rows = 0;
        foreach (string[] words in job.BenchCommands())
        {
            string? project = new[] { "bench/SkathIO.Rogue.Benchmarks.Jobs", "bench/SkathIO.Rogue.Benchmarks.Validation" }
                .FirstOrDefault(p => SmokeJob.RunsBench(string.Join(" ", words), p));
            rows += project is null ? 0 : StageCommand(words, project, transform, staged);
        }
        return rows;
    }

    private static int StageCommand(string[] words, string project, Func<long, long> transform, string staged)
    {
        int rows = 0;
        string[] filters = Option(words, "--filter");
        string[] suffixes = Option(words, "--exporters").Select(e => _jsonSuffixes.GetValueOrDefault(e)).OfType<string>().DefaultIfEmpty(_jsonSuffixes["json"]).ToArray();
        foreach ((string type, string csv) in NewestReports(project))
        {
            JsonObject[] benchmarks = Rows(type, csv, filters, transform).ToArray();
            rows += benchmarks.Length;
            if (benchmarks.Length > 0 || transform(0) < 0)
            {
                string json = Document(type, benchmarks).ToJsonString();
                Array.ForEach(suffixes, s => File.WriteAllText(Path.Combine(staged, type + s), json));
            }
        }
        return rows;
    }

    private static string[] Option(string[] words, string name) =>
        words.SkipWhile(w => !string.Equals(w, name, StringComparison.Ordinal)).Skip(1).TakeWhile(static w => !w.StartsWith('-')).ToArray();

    // Full type name to CSV path, for each class declared in the bench project, from the newest dated directory that has it.
    private static IEnumerable<(string Type, string Csv)> NewestReports(string project)
    {
        string source = string.Join("\n", Directory.GetFiles(Path.Combine(RepoFiles.Root, project), "*.cs", SearchOption.AllDirectories)
            .Where(static f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj)/")).Select(File.ReadAllText));
        return Directory.GetDirectories(Path.Combine(RepoFiles.Root, "bench", "results"))
            .Where(static d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}-\d{2}-\d{2}-"))
            .OrderByDescending(static d => Path.GetFileName(d), StringComparer.Ordinal)
            .SelectMany(static d => Directory.GetFiles(d, "*-report.csv"))
            .Select(static csv => (Type: Path.GetFileName(csv)[..^"-report.csv".Length], Csv: csv))
            .Where(r => Regex.IsMatch(source, @"\bclass\s+" + Regex.Escape(r.Type[(r.Type.LastIndexOf('.') + 1)..]) + @"\b"))
            .GroupBy(static r => r.Type, StringComparer.Ordinal)
            .Select(static g => g.First());
    }

    private static IEnumerable<JsonObject> Rows(string type, string csv, string[] filters, Func<long, long> transform)
    {
        string[][] table = File.ReadAllLines(csv).Where(static l => l.Length > 0).Select(CsvFields).ToArray();
        string[] header = table[0];
        int first = Array.IndexOf(header, "WarmupCount") + 1;
        int mean = Array.IndexOf(header, "Mean");
        int allocated = Array.IndexOf(header, "Allocated");
        Assert.True(first > 0 && mean >= first && allocated >= 0, csv + " has no WarmupCount, Mean or Allocated column.");
        string ns = type[..type.LastIndexOf('.')];
        string cls = type[(type.LastIndexOf('.') + 1)..];
        foreach (string[] row in table.Skip(1))
        {
            string method = row[0];
            long bytes = transform(Bytes(row[allocated]));
            if (bytes < 0 || (filters.Length > 0 && !filters.Any(f => Glob(f).IsMatch(ns + "." + cls + "." + method))))
            {
                continue;
            }
            string parameters = string.Join("&", Enumerable.Range(first, mean - first).Select(i => header[i] + "=" + row[i]));
            yield return Benchmark(type, method, parameters, bytes);
        }
    }

    private static JsonObject Benchmark(string type, string method, string parameters, long bytes)
    {
        string ns = type[..type.LastIndexOf('.')];
        string cls = type[(type.LastIndexOf('.') + 1)..];
        string shown = parameters.Replace("&", ", ", StringComparison.Ordinal);
        return new JsonObject
        {
            ["DisplayInfo"] = cls + "." + method + ": ShortRun(" + shown + ")",
            ["Namespace"] = ns,
            ["Type"] = cls,
            ["Method"] = method,
            ["MethodTitle"] = method,
            ["Parameters"] = parameters,
            ["FullName"] = ns + "." + cls + "." + method + (shown.Length > 0 ? "(" + shown + ")" : string.Empty),
            ["Memory"] = new JsonObject
            {
                ["Gen0Collections"] = 0,
                ["Gen1Collections"] = 0,
                ["Gen2Collections"] = 0,
                ["TotalOperations"] = 1,
                ["BytesAllocatedPerOperation"] = bytes,
            },
        };
    }

    private static JsonObject Document(string type, JsonObject[] benchmarks) => new JsonObject
    {
        ["Title"] = type + "-20261121-000000",
        ["HostEnvironmentInfo"] = new JsonObject { ["BenchmarkDotNetCaption"] = "BenchmarkDotNet", ["RuntimeVersion"] = ".NET 10.0" },
        ["Benchmarks"] = new JsonArray(benchmarks.Select(static b => (JsonNode)b).ToArray()),
    };

    private static Regex Glob(string pattern) =>
        new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);

    // BenchmarkDotNet's Allocated column: "0 B", "4064 B", "1.13 KB", "37.1 MB", in units of 1024.
    private static long Bytes(string cell)
    {
        Match m = Regex.Match(cell.Replace(",", string.Empty, StringComparison.Ordinal), @"^\s*(?<n>[0-9.]+)\s*(?<unit>B|KB|MB|GB)\s*$");
        Assert.True(m.Success, "Unreadable Allocated cell: " + cell);
        int power = Array.IndexOf(new[] { "B", "KB", "MB", "GB" }, m.Groups["unit"].Value);
        return (long)Math.Round(double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) * Math.Pow(1024, power));
    }

    private static string[] CsvFields(string line) =>
        Regex.Matches(line, @"(?<=^|,)(?:""(?<f>[^""]*)""|(?<f>[^,]*))").Select(static m => m.Groups["f"].Value).ToArray();
}
