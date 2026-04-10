using System;

namespace Volt;

/// <summary>
/// Receives parser events from VtStateMachine. Parameter spans are only valid
/// for the duration of the call — do not capture them.
/// </summary>
public interface IVtEventHandler
{
    /// <summary>Printable character (already UTF-8 decoded) to place at cursor.</summary>
    void Print(char ch);

    /// <summary>C0/C1 control byte (BEL, BS, HT, LF, CR, etc.).</summary>
    void Execute(byte ctrl);

    /// <summary>
    /// CSI final byte dispatched. Params list may contain 0 for omitted params
    /// (e.g. CSI ; 5 H → [0, 5]). Intermediates are any '?', '!', ' ', etc.
    /// collected between CSI and params.
    /// </summary>
    void CsiDispatch(char final, ReadOnlySpan<int> parameters, ReadOnlySpan<char> intermediates);

    /// <summary>ESC sequence that wasn't a CSI/OSC/DCS (e.g. ESC 7, ESC D).</summary>
    void EscDispatch(char final, ReadOnlySpan<char> intermediates);

    /// <summary>OSC command dispatched. First numeric param is the command id.</summary>
    void OscDispatch(int command, string data);
}
