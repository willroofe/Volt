using System;

namespace Volt;

/// <summary>
/// Paul Williams ANSI parser. See https://vt100.net/emu/dec_ansi_parser for the reference diagram.
/// Thread affinity: single thread (call Feed from one thread only).
/// </summary>
public sealed class VtStateMachine
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        SosPmApcString,
    }

    private readonly IVtEventHandler _h;
    private State _state = State.Ground;

    // Collected parameters for CSI/DCS
    private readonly int[] _params = new int[32];
    private int _paramCount;
    private bool _paramHasDigits;

    // Collected intermediates
    private readonly char[] _intermediates = new char[4];
    private int _intermediateCount;

    // OSC string buffer
    private readonly System.Text.StringBuilder _oscBuf = new(256);
    private const int OscMaxLength = 64 * 1024;

    public VtStateMachine(IVtEventHandler handler) { _h = handler; }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            Step(bytes[i]);
    }

    private void Step(byte b)
    {
        // Global anywhere-transitions (from Williams diagram)
        if (b == 0x18 || b == 0x1A) { _state = State.Ground; _h.Execute(b); return; }
        if (b == 0x1B) { EnterEscape(); return; }

        switch (_state)
        {
            case State.Ground: StepGround(b); break;
            // other states added in later tasks
            default: StepGround(b); break;
        }
    }

    private void StepGround(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b == 0x7F) { /* DEL — ignored in Ground */ return; }
        // 0x20..0x7E printable ASCII; 0x80+ UTF-8 continuation handled in Task 14
        _h.Print((char)b);
    }

    private void EnterEscape()
    {
        _paramCount = 0;
        _paramHasDigits = false;
        _intermediateCount = 0;
        _state = State.Escape;
    }
}
