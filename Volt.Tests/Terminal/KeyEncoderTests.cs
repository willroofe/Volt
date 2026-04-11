using System.Text;
using System.Windows.Input;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class KeyEncoderTests
{
    private static string Enc(Key k, ModifierKeys m = ModifierKeys.None)
    {
        var bytes = KeyEncoder.Encode(k, m);
        return bytes == null ? "<null>" : Encoding.ASCII.GetString(bytes);
    }

    [Fact] public void Enter_IsCarriageReturn() => Assert.Equal("\r", Enc(Key.Enter));
    [Fact] public void Tab_IsHt() => Assert.Equal("\t", Enc(Key.Tab));
    [Fact] public void Backspace_IsDel7F() => Assert.Equal("\x7f", Enc(Key.Back));
    [Fact] public void EscapeKey_IsEsc() => Assert.Equal("\x1b", Enc(Key.Escape));

    [Fact] public void Up_IsCsiA() => Assert.Equal("\x1b[A", Enc(Key.Up));
    [Fact] public void Down_IsCsiB() => Assert.Equal("\x1b[B", Enc(Key.Down));
    [Fact] public void Right_IsCsiC() => Assert.Equal("\x1b[C", Enc(Key.Right));
    [Fact] public void Left_IsCsiD() => Assert.Equal("\x1b[D", Enc(Key.Left));

    [Fact] public void ShiftUp_IsCsi1Sem2A() => Assert.Equal("\x1b[1;2A", Enc(Key.Up, ModifierKeys.Shift));
    [Fact] public void CtrlUp_IsCsi1Sem5A() => Assert.Equal("\x1b[1;5A", Enc(Key.Up, ModifierKeys.Control));

    [Fact] public void Home_IsCsiH() => Assert.Equal("\x1b[H", Enc(Key.Home));
    [Fact] public void End_IsCsiF() => Assert.Equal("\x1b[F", Enc(Key.End));
    [Fact] public void PageUp_IsCsi5Tilde() => Assert.Equal("\x1b[5~", Enc(Key.PageUp));
    [Fact] public void PageDown_IsCsi6Tilde() => Assert.Equal("\x1b[6~", Enc(Key.PageDown));

    [Fact] public void F1_IsSs3P() => Assert.Equal("\x1bOP", Enc(Key.F1));
    [Fact] public void F5_IsCsi15Tilde() => Assert.Equal("\x1b[15~", Enc(Key.F5));

    [Fact] public void CtrlA_IsSoh() => Assert.Equal("\x01", Enc(Key.A, ModifierKeys.Control));
    [Fact] public void CtrlC_IsEtx() => Assert.Equal("\x03", Enc(Key.C, ModifierKeys.Control));
    [Fact] public void CtrlZ_IsSub() => Assert.Equal("\x1a", Enc(Key.Z, ModifierKeys.Control));

    [Fact] public void UnmappedKey_ReturnsNull() => Assert.Null(KeyEncoder.Encode(Key.CapsLock, ModifierKeys.None));
}
