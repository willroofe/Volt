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
        if (b == 0x18 || b == 0x1A) { _state = State.Ground; _h.Execute(b); return; }
        if (b == 0x1B) { EnterEscape(); return; }

        switch (_state)
        {
            case State.Ground:             StepGround(b); break;
            case State.Escape:             StepEscape(b); break;
            case State.EscapeIntermediate: StepEscapeIntermediate(b); break;
            case State.CsiEntry:           StepCsiEntry(b); break;
            case State.CsiParam:           StepCsiParam(b); break;
            case State.CsiIntermediate:    StepCsiIntermediate(b); break;
            case State.CsiIgnore:          StepCsiIgnore(b); break;
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

    private void StepEscape(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); _state = State.EscapeIntermediate; return; }
        if (b == 0x5B) { _state = State.CsiEntry; return; }      // [
        if (b == 0x5D) { _oscBuf.Clear(); _state = State.OscString; return; } // ]
        if (b >= 0x30 && b <= 0x7E)
        {
            _h.EscDispatch((char)b, _intermediates.AsSpan(0, _intermediateCount));
            _state = State.Ground;
            return;
        }
    }

    private void StepEscapeIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); return; }
        if (b >= 0x30 && b <= 0x7E)
        {
            _h.EscDispatch((char)b, _intermediates.AsSpan(0, _intermediateCount));
            _state = State.Ground;
            return;
        }
        if (b <= 0x1F) { _h.Execute(b); return; }
    }

    private void StepCsiEntry(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x30 && b <= 0x39) { EnsureFirstParam(); AccumulateParamDigit(b); _state = State.CsiParam; return; }
        if (b == 0x3B) { EnsureFirstParam(); NextParam(); _state = State.CsiParam; return; }
        if (b >= 0x3C && b <= 0x3F) { CollectIntermediate((char)b); _state = State.CsiParam; return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); _state = State.CsiIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { DispatchCsi((char)b); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiParam(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x30 && b <= 0x39) { AccumulateParamDigit(b); return; }
        if (b == 0x3B) { NextParam(); return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); _state = State.CsiIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { DispatchCsi((char)b); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiIntermediate(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); return; }
        if (b >= 0x40 && b <= 0x7E) { DispatchCsi((char)b); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiIgnore(byte b)
    {
        if (b >= 0x40 && b <= 0x7E) { _state = State.Ground; return; }
    }

    private void EnsureFirstParam() { if (_paramCount == 0) { _paramCount = 1; _params[0] = 0; _paramHasDigits = false; } }
    private void NextParam() { if (_paramCount < _params.Length) { _paramCount++; _params[_paramCount - 1] = 0; _paramHasDigits = false; } }

    private void AccumulateParamDigit(byte b)
    {
        if (_paramCount == 0) _paramCount = 1;
        int idx = _paramCount - 1;
        long v = (long)_params[idx] * 10 + (b - (byte)'0');
        if (v > int.MaxValue) v = int.MaxValue;   // clamp overflow
        _params[idx] = (int)v;
        _paramHasDigits = true;
    }

    private void CollectIntermediate(char c)
    {
        if (_intermediateCount < _intermediates.Length)
            _intermediates[_intermediateCount++] = c;
    }

    private void DispatchCsi(char final)
    {
        _h.CsiDispatch(final,
            _params.AsSpan(0, Math.Max(_paramCount, 1)),
            _intermediates.AsSpan(0, _intermediateCount));
        _paramCount = 0;
        _paramHasDigits = false;
        _intermediateCount = 0;
        _state = State.Ground;
    }
}
