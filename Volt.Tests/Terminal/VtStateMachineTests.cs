using System.Collections.Generic;
using System.Text;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class VtStateMachineTests
{
    private sealed class RecordingHandler : IVtEventHandler
    {
        public List<string> Events = new();
        public void Print(char ch) => Events.Add($"Print:{ch}");
        public void Execute(byte ctrl) => Events.Add($"Exec:{ctrl:X2}");
        public void CsiDispatch(char final, System.ReadOnlySpan<int> p, System.ReadOnlySpan<char> i)
        {
            var ps = string.Join(",", p.ToArray());
            var ins = new string(i);
            Events.Add($"Csi:{ins}[{ps}]{final}");
        }
        public void EscDispatch(char final, System.ReadOnlySpan<char> i)
            => Events.Add($"Esc:{new string(i)}{final}");
        public void OscDispatch(int cmd, string data) => Events.Add($"Osc:{cmd}:{data}");
    }

    private static List<string> Feed(string bytes)
    {
        var h = new RecordingHandler();
        var sm = new VtStateMachine(h);
        sm.Feed(Encoding.ASCII.GetBytes(bytes));
        return h.Events;
    }

    [Fact]
    public void PlainAscii_EmitsPrintPerChar()
    {
        var events = Feed("Hi!");
        Assert.Equal(new[] { "Print:H", "Print:i", "Print:!" }, events);
    }

    [Fact]
    public void ControlBytes_EmitExecute()
    {
        var events = Feed("\b\t\n\r\a");
        Assert.Equal(new[] { "Exec:08", "Exec:09", "Exec:0A", "Exec:0D", "Exec:07" }, events);
    }
}
