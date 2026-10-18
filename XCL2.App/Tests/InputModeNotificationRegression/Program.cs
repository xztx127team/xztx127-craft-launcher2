using XCL2.App.Services;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

var current = new InputModeNotificationRequest();
var previous = new InputModeNotificationRequest();
foreach (var action in Enum.GetValues<InputModeNotificationAction>())
{
    var argument = current.Argument(action);
    Assert(current.TryGetAction(argument, out var actual) && actual == action,
        $"Current request must accept its own {action} action.");
    Assert(!current.TryGetAction(previous.Argument(action), out _),
        $"Previous launch must never control the current {action} selection.");
}
foreach (var argument in new string?[] { null, "", "Touch", "xcl2-input:",
    current.Argument(InputModeNotificationAction.Touch) + ":extra",
    current.Argument(InputModeNotificationAction.Touch).ToUpperInvariant() })
{
    Assert(!current.TryGetAction(argument, out _), "Malformed and modified activation arguments must be ignored.");
}
Assert(current.Tag.Length <= 16 && current.Tag != previous.Tag, "Each notification must have a distinct supported history tag.");
current.Close(); // selected, timed out, canceled, Esc, or launcher closed
current.Close(); // cleanup must be idempotent
Assert(!current.IsPending, "A completed request must be closed.");
foreach (var action in Enum.GetValues<InputModeNotificationAction>())
    Assert(!current.TryGetAction(current.Argument(action), out _),
        $"Delayed {action} activation must be ignored after completion.");
Assert(previous.IsPending, "Completing one launch must not complete another launch's prompt.");
Console.WriteLine($"PASS: {assertions} input-mode notification request assertions.");
