using XCL2.App.Services;

int checks = 0;
void Code(string markdown, string expected, string language = "", int start = 0)
{
    var lines = AiMarkdownCodeText.SplitLines(markdown);
    if (!AiMarkdownCodeText.TryRead(markdown, lines, start, out var actual, out var lang, out var next))
        throw new Exception("Opening fence was not recognized.");
    if (actual != expected) throw new Exception($"Code content changed: {System.Text.Json.JsonSerializer.Serialize(actual)}");
    if (lang != language) throw new Exception("Fence language changed.");
    if (next <= start) throw new Exception("Parser failed to make progress.");
    checks++;
}

Code("```text\none line\n```", "one line\n", "text");
Code("```\na\nb\n```", "a\nb\n");
Code("```\r\na\r\nb\r\n```", "a\r\nb\r\n");
Code("```\ra\rb\r```", "a\rb\r");
Code("```\r\n\tfirst  \n\nsecond\rthird\r\n```", "\tfirst  \n\nsecond\rthird\r\n");
Code("```\n" + new string('x', 20000), new string('x', 20000));
Code("```cs\n  no terminal newline", "  no terminal newline", "cs");
Code("```\n\n\n```", "\n\n");
Code("```\n```", "");
Code("````markdown\n```csharp\nvar x = 1;\n```\n````", "```csharp\nvar x = 1;\n```\n", "markdown");
Code("~~~json\n{\"value\": 1}\n~~~~", "{\"value\": 1}\n", "json");
Code("```\n```not-a-closing-fence\ncontent\n```", "```not-a-closing-fence\ncontent\n");
Code("Subject\r\n\r\n```text\r\nmail body\r\n```\r\nfooter", "mail body\r\n", "text", 2);
Code("`````\n ``` \n`````", " ``` \n");
Code("```\n    ```\nstill code\n```", "    ```\nstill code\n");
Code("```\n\t```\nstill code\n```", "\t```\nstill code\n");
Code("   ```text\ncontent\n   ```", "content\n", "text");
if (AiMarkdownCodeText.IsOpeningFence("inline `code`")) throw new Exception("Inline code became a code block.");
if (AiMarkdownCodeText.IsOpeningFence("``too short")) throw new Exception("Short fence became a code block.");
if (AiMarkdownCodeText.IsOpeningFence("    ```")) throw new Exception("Indented code became a fence.");
if (AiMarkdownCodeText.IsOpeningFence("\t```")) throw new Exception("Tab-indented code became a fence.");
checks += 4;
Console.WriteLine($"PASS: {checks} original-newline, whitespace, streaming, long-line and fenced-code checks.");
