using System;
using System.Collections.Generic;

namespace Volt.Samples;

public sealed class GrammarSample
{
    public string Name { get; init; } = "C#";

    public void Print(int count)
    {
        string message = $"Language {Name}: {count}";
        Console.WriteLine(message);
    }
}
