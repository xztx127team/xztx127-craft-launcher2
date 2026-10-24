using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

namespace XCL2.App.Services;

public sealed record AprilReadingQuestion(string Passage, string Question, string[] Options, int Answer);

/// <summary>本地趣味流程，不是安全认证，也不包含账号、网络或机器指纹。</summary>
public sealed class AprilFoolsExitChallengeState
{
    public const int ReadingCount = 7;
    public const int MiniGameCount = 12;
    public const int StageCount = ReadingCount + MiniGameCount + 1;
    public int Stage { get; private set; }
    public int Failures { get; private set; }
    public bool ShortcutUnlocked { get; private set; }
    public bool Completed => Stage == StageCount;
    public bool CanClose => Completed || ShortcutUnlocked;
    public bool IsReading => Stage < ReadingCount;
    public bool IsHumanCheck => Stage == StageCount - 1;
    // Process-local only: never serialized into AppConfig, reminders, history or launcher logs.
    public static string SessionPassword { get; } = GenerateSessionPassword();
    [ModuleInitializer]
    internal static void InitializeSessionPassword() => _ = SessionPassword;
    internal static string GenerateSessionPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    public static bool IsShortcutCode(string? code)
    {
        var value = code?.Trim();
        return value == "跳过" || string.Equals(value, "xztx127", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, SessionPassword, StringComparison.OrdinalIgnoreCase);
    }
    public static string NormalizePuzzleAnswer(string? value)
    {
        try { return string.Concat((value ?? "").Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant(); }
        catch (ArgumentException) { return ""; } // malformed pasted Unicode is a wrong answer, not a UI exception
    }
    public static bool MatchesPuzzleAnswer(string? value, string expected)
        => NormalizePuzzleAnswer(expected).Length > 0 && NormalizePuzzleAnswer(value) == NormalizePuzzleAnswer(expected);
    public static string ReverseLettersWithoutDigits(string token)
        => new(token.Where(char.IsLetter).Reverse().ToArray());
    public static string ReverseVerificationCode(string code) => new(code.Reverse().ToArray());
    public bool TryUnlockShortcut(string? code)
    {
        if (!IsShortcutCode(code)) return false;
        ShortcutUnlocked = true;
        return true;
    }
    public bool ShouldOfferConfigExit(TimeSpan elapsed) => Failures >= 2 || elapsed >= TimeSpan.FromSeconds(20);
    public void Fail() { if (!Completed) Failures++; }
    public bool AnswerReading(int answer)
    {
        if (!IsReading) return false;
        if (answer != Questions[Stage].Answer) { Fail(); return false; }
        Pass(); return true;
    }
    public void Pass() { if (!Completed) Stage++; }

    public static readonly IReadOnlyList<AprilReadingQuestion> Questions = new AprilReadingQuestion[]
    {
        new("胖虎说：甲门通向‘继续整蛊’，乙门通向‘继续关闭彩蛋设置’，丙门通向‘再读一遍’。你要关闭彩蛋设置，但还没走完挑战。", "应该走哪一扇门？", new[]{"甲门","乙门","丙门"}, 1),
        new("告示写着：只有不选择‘留下’的人，才会进入下一关。‘稍后再说’属于留下。", "哪个选择能前往下一关？", new[]{"留下","稍后再说","继续关闭彩蛋设置"}, 2),
        new("小虎把三张纸条排好：先读规则，再选答案，最后继续。读规则不等于答题，选错也不会自动继续。", "现在已经读完并选对答案，下一步是什么？", new[]{"继续","重新开始全部挑战","关闭账号"}, 0),
        new("这里所有问题都只是本地彩蛋。不会授予命令权限，不会提交个人信息，也不会替你接受新的协议。", "本次回答会授予什么权限？", new[]{"所有Shell权限","不会授予任何权限","读取所有文件"}, 1),
        new("红牌写‘不要选我’，蓝牌写‘请选红牌’，绿牌写‘按题目要求作答’。题目明确要求：选择绿牌。", "你应该选择哪张？", new[]{"红牌","蓝牌","绿牌"}, 2),
        new("通关后可以关闭彩蛋设置，正常启动仍要检查账号、Java和游戏文件。通关不会让这些检查消失。", "通关后会怎样？", new[]{"关闭彩蛋设置并继续原有检查","省略账号检查","自动执行未授权命令"}, 0),
        new("阅读理解已经来到第七题。接下来是十二个小游戏，最后还有一次本地人机趣味验证。通关用于关闭彩蛋设置；F1只结束当天整蛊。右侧“查看密码”显示的是本次启动专用密码。", "不用完成挑战也能立即结束当天整蛊的按键是？", new[]{"F11","F1","Delete"}, 1)
    };
}
