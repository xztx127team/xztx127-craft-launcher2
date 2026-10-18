using System.Text.Json;
using XCL2.App.Services;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new Exception("FAIL: " + message);
}
string Plan(string payload) => AiPlanProtocol.OpenMarker + "\n" + payload + "\n" + AiPlanProtocol.CloseMarker;
JsonElement Json(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}
AiControlProtocol.Result ParsedPlan(string payload) => AiControlProtocol.Parse(Plan(payload));
void InvalidBlock(string text, string message)
{
    var result = AiControlProtocol.Parse(text);
    Check(result.HasMarker && !result.IsValid && result.Request.ValueKind == JsonValueKind.Undefined, message);
}
void InvalidSteps(string steps, string message)
{
    var input = Json(steps);
    Check(AiPlanProtocol.ValidateSteps(input) != null, message);
    bool rejected = false;
    try { AiPlanProtocol.NormalizeSteps(input); } catch (ArgumentException) { rejected = true; }
    Check(rejected, message + " also rejected by persistence normalization");
}

foreach (var (prefix, status) in new[]
{
    ("○", "in_progress"), ("◯", "in_progress"), ("●", "in_progress"), ("◉", "in_progress"),
    ("✓", "completed"), ("✔", "completed"), ("✔️", "completed"), ("✅", "completed"), ("☑", "completed"),
    ("–", "pending"), ("—", "pending"), ("−", "pending"), ("-", "pending"),
    ("- [ ]", "pending"), ("- [x]", "completed"), ("- [X]", "completed"), ("- [~]", "in_progress"),
    ("[ ]", "pending"), ("[x]", "completed"), ("[/]", "in_progress"),
    ("in_progress:", "in_progress"), ("COMPLETED:", "completed"), ("pending:", "pending"), ("blocked:", "blocked"),
    ("1. ○", "in_progress"), ("2) - [ ]", "pending"), ("3、- [x]", "completed"), ("* ○", "in_progress")
})
{
    var parsed = ParsedPlan(prefix + " [inspect] 核验项目");
    Check(parsed.IsValid && parsed.Kind == "ACCESS" && AiControlProtocol.String(parsed.Request, "action") == "update_task", prefix + " parses to only update_task");
    var step = AiPlanProtocol.NormalizeSteps(parsed.Request.GetProperty("steps")).Single();
    Check(step.Id == "inspect" && step.Title == "核验项目" && step.Status == status, prefix + " keeps explicit identity and status");
}

foreach (var fence in new[] { "```", "~~~", "````" })
foreach (var language in new[] { "", "text", "json", "plan", "xcl2", "plaintext", "JSON" })
{
    var raw = "先核验文件\n" + fence + language + "\n\n  " + Plan("- [ ] 核验文件") + "\n" + fence + "\n ";
    var parsed = AiControlProtocol.Parse(raw);
    Check(parsed.IsValid && parsed.DisplayText == "先核验文件", fence + language + " terminal plan fence accepted and hidden");
    Check(AiControlProtocol.Strip(raw, "ACCESS") == "先核验文件", "fenced plan strip remains consistent");
    var command = "Write-Output '{quoted} <<XCL2_ASK: data'";
    var request = "<<XCL2_ACCESS:" + JsonSerializer.Serialize(new { action = "run_command", command }) + ">>";
    var parsedCommand = AiControlProtocol.Parse(fence + language + "\n" + request + "\n" + fence);
    Check(parsedCommand.IsValid && AiControlProtocol.String(parsedCommand.Request, "command") == command,
        "fenced JSON keeps quoted command and marker-like string byte-for-byte");
}

foreach (var invalid in new[]
{
    Plan(""), Plan("○ one\n○ two"), Plan("✓ [same] one\n- [same] two"),
    Plan("○ [broken title"), Plan("○ "), Plan("1. no explicit status"), Plan("-rf executable-looking text"),
    Plan("shell: echo forbidden"), Plan("○ one") + "\ntrailing prose", Plan("○ one") + "\n" + Plan("- two"),
    Plan("○ one") + "\n<<XCL2_ACCESS:{\"action\":\"run_command\",\"command\":\"echo forbidden\"}>>",
    "```text\n" + Plan("○ one"), "```text\n" + Plan("○ one") + "\n```\ntrailing prose",
    "```text\nsource file content\n" + Plan("○ one") + "\n```",
    "```html\n" + Plan("○ one") + "\n```", "```text\n" + Plan("○ one") + "\n~~~",
    Plan("○ " + new string('x', 1001)), Plan("✓ done | evidence: " + new string('x', 4001)),
    Plan("- one\nnotes: first\nNOTES: second"), Plan("- one\nnext_action: first\nNEXT_ACTION: second"),
    Plan("- one\nnotes: " + new string('x', 20001)), Plan("- one\nnext_action: " + new string('x', 10001)),
    Plan(string.Join("\n", Enumerable.Range(1, 101).Select(i => "- step " + i))),
    Plan("- " + new string('x', AiPlanProtocol.MaxBlockChars)),
    "<<XCL2_ACCESS:{\"action\":\"write_workspace_file\",\"path\":\"out.html\",\"content\":\"<div class=\"x\">\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[]},{\"action\":\"run_command\",\"command\":\"forbidden\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[]}>>\n" + Plan("○ one"),
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[],\"steps\":[]}>>"
}) InvalidBlock(invalid, "malformed/mixed/nonterminal blocks never produce usable requests");
Check(!AiControlProtocol.Parse("本轮先把执行计划落盘到任务：1. 核验，2. 修改").HasMarker,
    "ordinary prose never becomes a saved plan or an executable request");
Check(ParsedPlan(string.Join("\n", Enumerable.Range(1, 100).Select(i => "- step " + i))).IsValid,
    "100-step boundary accepted");
Check(ParsedPlan("- " + new string('x', 1000)).IsValid, "1000-character title accepted");
Check(ParsedPlan("✓ done | evidence: " + new string('x', 4000)).IsValid, "4000-character evidence accepted");
Check(ParsedPlan("- " + string.Concat(Enumerable.Repeat("1. ", 250))).IsValid,
    "deep presentation-like title is bounded data, not recursive parser work");

foreach (var steps in new[]
{
    "null", "{}", "[null]", "[\"title\"]", "[{}]", "[{\"title\":\" \"}]", "[{\"title\":null}]",
    "[{\"title\":1}]", "[{\"title\":\"one\",\"Title\":\"two\"}]", "[{\"title\":\"one\",\"status\":null}]",
    "[{\"title\":\"one\",\"status\":\"DONE\"}]", "[{\"title\":\"one\",\"evidence\":false}]",
    "[{\"title\":\"one\",\"id\":\" \"}]", "[{\"title\":\"one\",\"id\":null}]",
    "[{\"title\":\"one\",\"id\":\"same\"},{\"title\":\"two\",\"id\":\" same \"}]",
    "[{\"title\":\"one\",\"status\":\"in_progress\"},{\"title\":\"two\",\"status\":\"in_progress\"}]"
}) InvalidSteps(steps, "invalid task steps rejected before persistence");
foreach (var steps in new[]
{
    JsonSerializer.Serialize(new[] { new { title = new string('x', 1001) } }),
    JsonSerializer.Serialize(new[] { new { title = "one", evidence = new string('x', 4001) } }),
    JsonSerializer.Serialize(new[] { new { title = "one", id = new string('x', 129) } }),
    JsonSerializer.Serialize(Enumerable.Range(1, 101).Select(i => new { title = "step " + i }))
}) InvalidSteps(steps, "JSON step boundaries match text-plan boundaries");
var invalidTitleRequest = Json("{\"action\":\"update_task\",\"steps\":[{\"status\":\"pending\"}]}");
Check(AiControlProtocol.ValidateAccessArguments(invalidTitleRequest) != null, "ACCESS validation requires title too");
Check(AiControlProtocol.ValidateAccessArguments(Json("{\"action\":\"update_task\",\"notes\":\"checkpoint\"}")) == null,
    "legacy metadata-only update_task remains valid");

var defaults = AiPlanProtocol.NormalizeSteps(Json("[{\"Title\":\" one \"},{\"title\":\"two\"}]"));
Check(defaults[0] == new AiPlanProtocol.Step("plan-1", "one", "pending", "")
    && defaults[1].Id == "plan-2", "case-insensitive titles normalize with deterministic initial IDs and default state");
Check(AiPlanProtocol.NormalizeSteps(Json("[{\"title\":\"data only\",\"command\":\"forbidden\",\"full_access\":true}]")).Single()
    == new AiPlanProtocol.Step("plan-1", "data only", "pending", ""),
    "normalization constructs only plan fields and cannot carry command or permission changes");
var previous = new[]
{
    new AiPlanProtocol.Step("inspect", "核验", "completed", "已读取现状"),
    new AiPlanProtocol.Step("change", "修改", "in_progress", "正在修改"),
    new AiPlanProtocol.Step("finish", "报告", "pending", "")
};
var reordered = AiPlanProtocol.NormalizeSteps(Json("[{\"title\":\"报告\"},{\"title\":\"核验\"},{\"title\":\"修改\"}]"), previous);
Check(reordered.Select(step => step.Id).SequenceEqual(new[] { "finish", "inspect", "change" }),
    "omitted IDs match unique saved titles across reordering");
Check(reordered[1].Status == "completed" && reordered[1].Evidence == "已读取现状" && reordered[2].Status == "in_progress",
    "omitted status/evidence retain saved state rather than fabricated completion");
var renamed = AiPlanProtocol.NormalizeSteps(Json("[{\"ID\":\"inspect\",\"TITLE\":\"重新核验\"}]"), previous).Single();
Check(renamed.Id == "inspect" && renamed.Title == "重新核验" && renamed.Status == "completed" && renamed.Evidence == "已读取现状",
    "explicit ID preserves omitted state during title edit");
var cleared = AiPlanProtocol.NormalizeSteps(Json("[{\"id\":\"inspect\",\"title\":\"核验\",\"status\":\"pending\",\"evidence\":\"\"}]"), previous).Single();
Check(cleared.Status == "pending" && cleared.Evidence == "", "explicit state/evidence overrides remain intentional");
var collisions = AiPlanProtocol.NormalizeSteps(Json("[{\"id\":\"plan-2\",\"title\":\"explicit\"},{\"title\":\"new\"}]"),
    new[] { new AiPlanProtocol.Step("plan-1", "old", "completed", "old evidence") });
Check(collisions[1].Id == "plan-3" && collisions[1].Status == "pending" && collisions[1].Evidence == "",
    "generated IDs avoid old and later explicit IDs, with no state transferred to another title");
var repeated = AiPlanProtocol.NormalizeSteps(Json("[{\"title\":\"same\"},{\"title\":\"same\"}]"),
    new[] { new AiPlanProtocol.Step("a", "same", "completed", "a"), new AiPlanProtocol.Step("b", "same", "pending", "b") });
Check(repeated.Select(step => step.Id).Distinct().Count() == 2 && repeated.All(step => step.Status == "pending" && step.Evidence == ""),
    "ambiguous repeated titles do not inherit arbitrary evidence or completion");
var repeatedNew = AiPlanProtocol.NormalizeSteps(Json("[{\"title\":\"same\"},{\"title\":\"same\"}]"),
    new[] { new AiPlanProtocol.Step("a", "same", "completed", "prior evidence") });
Check(repeatedNew.All(step => step.Id != "a" && step.Status == "pending" && step.Evidence == ""),
    "a repeated incoming title cannot arbitrarily claim a unique old step's completion");
var textReordered = ParsedPlan("- 报告\n✓ 核验 | evidence: 已读取现状\n○ 修改");
Check(AiPlanProtocol.NormalizeSteps(textReordered.Request.GetProperty("steps"), previous).Select(step => step.Id)
    .SequenceEqual(new[] { "finish", "inspect", "change" }), "text-plan IDs normalize with the same persistence identity rules");
var textOmitted = ParsedPlan("✓ 核验\n○ 修改\n- 报告");
Check(!textOmitted.Request.TryGetProperty("notes", out _) && !textOmitted.Request.TryGetProperty("next_action", out _)
    && AiPlanProtocol.NormalizeSteps(textOmitted.Request.GetProperty("steps"), previous)[0].Evidence == "已读取现状",
    "omitted text-plan metadata and evidence preserve existing checkpoints rather than clearing them");
bool conflictRejected = false;
try { AiPlanProtocol.NormalizeSteps(Json("[{\"id\":\"change\",\"title\":\"修改\"},{\"title\":\"新任务\",\"status\":\"in_progress\"}]"), previous); }
catch (ArgumentException) { conflictRejected = true; }
Check(conflictRejected, "inherited active state cannot silently combine with a new active step");
Check(AiPlanProtocol.NormalizeSteps(Json("[]"), previous).Count == 0, "explicit empty list can clear steps as before");

// Persistence requires a complete old-ID set, unless removals are explicitly declared.
var partialRequest = Json("{\"action\":\"update_task\",\"steps\":[{\"id\":\"inspect\",\"title\":\"核验\"}]}");
var partial = AiPlanProtocol.NormalizeSteps(partialRequest.GetProperty("steps"), previous);
bool omissionRejected = false;
try { AiPlanProtocol.EnsureCompleteReplacement(previous, partial, partialRequest); } catch (ArgumentException) { omissionRejected = true; }
Check(omissionRejected, "a truncated replacement cannot silently remove the omitted old steps");
var explicitRemoval = ParsedPlan("✓ [inspect] 核验\nremove_step: [change]\nremove_step: [finish]");
Check(explicitRemoval.IsValid && explicitRemoval.Request.GetProperty("remove_step_ids").GetArrayLength() == 2,
    "text plan can explicitly declare intentional removals without JSON");
AiPlanProtocol.EnsureCompleteReplacement(previous,
    AiPlanProtocol.NormalizeSteps(explicitRemoval.Request.GetProperty("steps"), previous), explicitRemoval.Request);
Check(AiControlProtocol.ValidateAccessArguments(Json("{\"action\":\"update_task\",\"remove_step_ids\":[\"inspect\"]}")) != null,
    "removals cannot be applied without an explicit complete replacement list");

Console.WriteLine($"PASS: {assertions} Plan parser and normalization assertions");
