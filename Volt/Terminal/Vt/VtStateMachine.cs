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

    // Collected intermediates
    private readonly char[] _intermediates = new char[4];
    private int _intermediateCount;

    // OSC string buffer
    private readonly System.Text.StringBuilder _oscBuf = new(256);
    private const int OscMaxLength = 64 * 1024;
    private bool _oscEscapePending;

    // DCS state tracking
    private bool _dcsEscapePending;

    // UTF-8 decode state
    private int _utf8Remaining;
    private int _utf8Codepoint;

    public VtStateMachine(IVtEventHandler handler) { _h = handler; }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            Step(bytes[i]);
    }

    private void Step(byte b)
    {
        // DCS ST terminator: ESC \ while in DCS → Ground
        if (_dcsEscapePending)
        {
            if (b == 0x5C) { _state = State.Ground; _dcsEscapePending = false; return; }
            _dcsEscapePending = false;
            // fall through
        }

        // OSC ST terminator: ESC \ while in OSC → DispatchOsc
        if (_state == State.OscString && _oscEscapePending)
        {
            if (b == 0x5C) { DispatchOsc(); return; }
            _oscEscapePending = false;
            if (_oscBuf.Length < OscMaxLength) _oscBuf.Append('\u001b');
            // fall through
        }

        if (b == 0x18 || b == 0x1A) { _state = State.Ground; _h.Execute(b); return; }
        if (b == 0x1B)
        {
            if (_state == State.OscString) { _oscEscapePending = true; return; }
            if (_state == State.DcsPassthrough || _state == State.DcsIgnore
                || _state == State.DcsEntry || _state == State.DcsParam || _state == State.DcsIntermediate)
            {
                _dcsEscapePending = true;
                return;
            }
            EnterEscape();
            return;
        }

        switch (_state)
        {
            case State.Ground:             StepGround(b); break;
            case State.Escape:             StepEscape(b); break;
            case State.EscapeIntermediate: StepEscapeIntermediate(b); break;
            case State.CsiEntry:           StepCsiEntry(b); break;
            case State.CsiParam:           StepCsiParam(b); break;
            case State.CsiIntermediate:    StepCsiIntermediate(b); break;
            case State.CsiIgnore:          StepCsiIgnore(b); break;
            case State.OscString:          StepOsc(b); break;
            case State.DcsEntry:           StepDcsEntry(b); break;
            case State.DcsParam:           StepDcsParam(b); break;
            case State.DcsIntermediate:    StepDcsIntermediate(b); break;
            case State.DcsPassthrough:     StepDcsPassthrough(b); break;
            case State.DcsIgnore:          StepDcsIgnore(b); break;
        }
    }

    private void StepGround(byte b)
    {
        if (b <= 0x1F) { FlushUtf8Error(); _h.Execute(b); return; }
        if (b == 0x7F) { FlushUtf8Error(); return; }

        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) != 0x80) { _h.Print('\uFFFD'); _utf8Remaining = 0; StepGround(b); return; }
            _utf8Codepoint = (_utf8Codepoint << 6) | (b & 0x3F);
            _utf8Remaining--;
            if (_utf8Remaining == 0)
            {
                if (_utf8Codepoint <= 0xFFFF)
                    _h.Print((char)_utf8Codepoint);
                else
                    _h.Print('\uFFFD'); // surrogate pairs deferred; v1 treats them as replacement
            }
            return;
        }

        if (b < 0x80) { _h.Print((char)b); return; }

        if ((b & 0xE0) == 0xC0) { _utf8Codepoint = b & 0x1F; _utf8Remaining = 1; return; }
        if ((b & 0xF0) == 0xE0) { _utf8Codepoint = b & 0x0F; _utf8Remaining = 2; return; }
        if ((b & 0xF8) == 0xF0) { _utf8Codepoint = b & 0x07; _utf8Remaining = 3; return; }
        _h.Print('\uFFFD');
    }

    private void FlushUtf8Error()
    {
        if (_utf8Remaining > 0) { _h.Print('\uFFFD'); _utf8Remaining = 0; }
    }

    private void EnterEscape()
    {
        _paramCount = 0;
        _intermediateCount = 0;
        _state = State.Escape;
    }

    private void StepEscape(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); _state = State.EscapeIntermediate; return; }
        if (b == 0x5B) { _state = State.CsiEntry; return; }      // [
        if (b == 0x5D) { _oscBuf.Clear(); _state = State.OscString; return; } // ]
        if (b == 0x50) { _state = State.DcsEntry; return; }      // P
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

    private void EnsureFirstParam() { if (_paramCount == 0) { _paramCount = 1; _params[0] = 0; } }
    private void NextParam() { if (_paramCount < _params.Length) { _paramCount++; _params[_paramCount - 1] = 0; } }

    private void AccumulateParamDigit(byte b)
    {
        EnsureFirstParam();  // Initialize if first digit
        int idx = _paramCount - 1;
        long v = (long)_params[idx] * 10 + (b - (byte)'0');
        if (v > int.MaxValue) v = int.MaxValue;   // clamp overflow
        _params[idx] = (int)v;
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
        _intermediateCount = 0;
        _state = State.Ground;
    }

    private void StepOsc(byte b)
    {
        // BEL (0x07) terminates OSC
        if (b == 0x07) { DispatchOsc(); return; }
        // Otherwise accumulate up to the length limit
        if (_oscBuf.Length < OscMaxLength)
            _oscBuf.Append((char)b);
    }

    private void DispatchOsc()
    {
        var full = _oscBuf.ToString();
        int semi = full.IndexOf(';');
        int cmd = 0;
        string data = "";
        if (semi < 0)
        {
            int.TryParse(full, out cmd);
        }
        else
        {
            int.TryParse(full.AsSpan(0, semi), out cmd);
            data = full.Substring(semi + 1);
        }
        _h.OscDispatch(cmd, data);
        _oscBuf.Clear();
        _oscEscapePending = false;
        _state = State.Ground;
    }

    private void StepDcsEntry(byte b)
    {
        if (b >= 0x30 && b <= 0x39) { _state = State.DcsParam; return; }
        if (b == 0x3B) { _state = State.DcsParam; return; }
        if (b >= 0x3C && b <= 0x3F) { _state = State.DcsParam; return; }
        if (b >= 0x20 && b <= 0x2F) { _state = State.DcsIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsPassthrough; return; }
        _state = State.DcsIgnore;
    }

    private void StepDcsParam(byte b)
    {
        if (b >= 0x30 && b <= 0x39) return;
        if (b == 0x3B) return;
        if (b >= 0x20 && b <= 0x2F) { _state = State.DcsIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsPassthrough; return; }
        if (b >= 0x3C && b <= 0x3F) { _state = State.DcsIgnore; return; }
    }

    private void StepDcsIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) return;
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsPassthrough; return; }
        _state = State.DcsIgnore;
    }

    private void StepDcsPassthrough(byte b) { /* consume silently */ }
    private void StepDcsIgnore(byte b) { /* consume silently */ }
}
