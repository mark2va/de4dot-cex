/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;

namespace de4dot.blocks.cflow {
public class UInt64Value : Value {
public static readonly UInt64Value Zero = new UInt64Value(0);
public static readonly UInt64Value One = new UInt64Value(1);

internal const ulong NO_UNKNOWN_BITS = ulong.MaxValue;
public readonly ulong Value;
public readonly ulong ValidMask;

public UInt64Value(ulong value)
: base(ValueType.UInt64) {
this.Value = value;
this.ValidMask = NO_UNKNOWN_BITS;
}

public UInt64Value(ulong value, ulong validMask)
: base(ValueType.UInt64) {
this.Value = value;
this.ValidMask = validMask;
}

bool HasUnknownBits() {
return ValidMask != NO_UNKNOWN_BITS;
}

public bool AllBitsValid() {
return !HasUnknownBits();
}

bool IsBitValid(int n) {
return IsBitValid(ValidMask, n);
}

static bool IsBitValid(ulong validMask, int n) {
return (validMask & (1UL << n)) != 0;
}

bool AreBitsValid(ulong bitsToTest) {
return (ValidMask & bitsToTest) == bitsToTest;
}

public static UInt64Value CreateUnknown() {
return new UInt64Value(0, 0UL);
}

public bool IsZero() {
return HasValue(0);
}

public bool IsNonZero() {
return ((ulong)Value & ValidMask) != 0;
}

public bool HasValue(ulong value) {
return AllBitsValid() && this.Value == value;
}

public static UInt64Value Conv_U8(Int32Value a) {
ulong value = (ulong)(uint)a.Value;
ulong validMask = a.ValidMask | (NO_UNKNOWN_BITS << 32);
return new UInt64Value(value, validMask);
}

public static UInt64Value Conv_U8(Int64Value a) {
return new UInt64Value((ulong)a.Value, a.ValidMask);
}

public static UInt64Value Conv_U8(UInt64Value a) {
return a;
}

public static UInt64Value Conv_U8(Real8Value a) {
if (!a.IsValid)
return CreateUnknown();
return new UInt64Value((ulong)a.Value);
}

public static Int32Value Conv_Ovf_I1(UInt64Value a) {
if (!a.AreBitsValid(NO_UNKNOWN_BITS << 7) ||
a.Value > (ulong)sbyte.MaxValue)
return Int32Value.CreateUnknown();
return Int32Value.Conv_I1((long)a.Value);
}

public static Int32Value Conv_Ovf_I1_Un(UInt64Value a) {
if (!a.AreBitsValid(NO_UNKNOWN_BITS << 7) ||
a.Value > (ulong)sbyte.MaxValue)
return Int32Value.CreateUnknown();
return Int32Value.Conv_I1((long)a.Value);
}

public static Int32Value Conv_Ovf_I2(UInt64Value a) {
if (!a.AreBitsValid(NO_UNKNOWN_BITS << 15) ||
a.Value > (ulong)short.MaxValue)
return Int32Value.CreateUnknown();
return Int32Value.Conv_I2((long)a.Value);
}

public static Int32Value Conv_Ovf_I2_Un(UInt64Value a) {
if (!a.AreBitsValid(NO_UNKNOWN_BITS << 15) ||
a.Value > (ulong)short.MaxValue)
return Int32Value.CreateUnknown();
return Int32Value.Conv_I2((long)a.Value);
}

public static Int32Value Conv_Ovf_I4(UInt64Value a) {
if (!a.AreBitsValid(NO_UNKNOWN_BITS << 31) ||
a.Value > (ulong)int.MaxValue)
return Int32Value.CreateUnknown();
return Int32Value.Conv_I4((long)a.Value);
}

public static Int32Value Conv_Ovf_I4_Un(UInt64Value a) {
if (!a.AreBitsValid(NO_UNKNOWN_BITS << 31) ||
a.Value > (ulong)int.MaxValue)
return Int32Value.CreateUnknown();
return Int32Value.Conv_I4((long)a.Value);
}

public static Int64Value Conv_Ovf_I8(UInt64Value a) {
if (!IsBitValid(a.ValidMask, 63) || a.Value > (ulong)long.MaxValue)
return Int64Value.CreateUnknown();
return new Int64Value((long)a.Value, a.ValidMask);
}

public static Int64Value Conv_Ovf_I8_Un(UInt64Value a) {
if (!IsBitValid(a.ValidMask, 63) || a.Value > (ulong)long.MaxValue)
return Int64Value.CreateUnknown();
return new Int64Value((long)a.Value, a.ValidMask);
}

public static UInt64Value Conv_Ovf_U8(UInt64Value a) {
return a;
}

public static UInt64Value Conv_Ovf_U8_Un(UInt64Value a) {
return a;
}

public static Real8Value Conv_R_Un(UInt64Value a) {
if (a.AllBitsValid())
return new Real8Value((float)a.Value);
return Real8Value.CreateUnknown();
}

public static Real8Value Conv_R4(UInt64Value a) {
if (a.AllBitsValid())
return new Real8Value((float)a.Value);
return Real8Value.CreateUnknown();
}

public static Real8Value Conv_R8(UInt64Value a) {
if (a.AllBitsValid())
return new Real8Value((double)a.Value);
return Real8Value.CreateUnknown();
}

public static UInt64Value Add(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return new UInt64Value(a.Value + b.Value);
if (ReferenceEquals(a, b))
return new UInt64Value(a.Value << 1, (a.ValidMask << 1) | 1);
return CreateUnknown();
}

public static UInt64Value Sub(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return new UInt64Value(a.Value - b.Value);
if (ReferenceEquals(a, b))
return Zero;
return CreateUnknown();
}

public static UInt64Value Mul(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return new UInt64Value(a.Value * b.Value);
if (a.IsZero() || b.IsZero())
return Zero;
if (a.HasValue(1))
return b;
if (b.HasValue(1))
return a;
return CreateUnknown();
}

public static UInt64Value Div(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid()) {
try {
return new UInt64Value(a.Value / b.Value);
}
catch (ArithmeticException) {
return CreateUnknown();
}
}
if (ReferenceEquals(a, b) && a.IsNonZero())
return One;
if (b.HasValue(1))
return a;
return CreateUnknown();
}

public static UInt64Value Div_Un(UInt64Value a, UInt64Value b) {
return Div(a, b);
}

public static UInt64Value Rem(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid()) {
try {
return new UInt64Value(a.Value % b.Value);
}
catch (ArithmeticException) {
return CreateUnknown();
}
}
if ((ReferenceEquals(a, b) && a.IsNonZero()) || b.HasValue(1))
return Zero;
return CreateUnknown();
}

public static UInt64Value Rem_Un(UInt64Value a, UInt64Value b) {
return Rem(a, b);
}

public static UInt64Value Neg(UInt64Value a) {
if (a.AllBitsValid())
return new UInt64Value(unchecked(-a.Value));
return CreateUnknown();
}

public static UInt64Value Add_Ovf(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid()) {
try {
return new UInt64Value(checked(a.Value + b.Value));
}
catch (OverflowException) {
}
}
return CreateUnknown();
}

public static UInt64Value Add_Ovf_Un(UInt64Value a, UInt64Value b) {
return Add_Ovf(a, b);
}

public static UInt64Value Sub_Ovf(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid()) {
try {
return new UInt64Value(checked(a.Value - b.Value));
}
catch (OverflowException) {
}
}
return CreateUnknown();
}

public static UInt64Value Sub_Ovf_Un(UInt64Value a, UInt64Value b) {
return Sub_Ovf(a, b);
}

public static UInt64Value Mul_Ovf(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid()) {
try {
return new UInt64Value(checked(a.Value * b.Value));
}
catch (OverflowException) {
}
}
return CreateUnknown();
}

public static UInt64Value Mul_Ovf_Un(UInt64Value a, UInt64Value b) {
return Mul_Ovf(a, b);
}

public static UInt64Value And(UInt64Value a, UInt64Value b) {
ulong av = a.Value, bv = b.Value;
ulong am = a.ValidMask, bm = b.ValidMask;
return new UInt64Value(av & bv, (am & bm) | (((ulong)av & am) ^ am) | (((ulong)bv & bm) ^ bm));
}

public static UInt64Value Or(UInt64Value a, UInt64Value b) {
ulong av = a.Value, bv = b.Value;
ulong am = a.ValidMask, bm = b.ValidMask;
return new UInt64Value(av | bv, (am & bm) | ((ulong)av & am) | ((ulong)bv & bm));
}

public static UInt64Value Xor(UInt64Value a, UInt64Value b) {
if (ReferenceEquals(a, b))
return Zero;
ulong av = a.Value, bv = b.Value;
ulong am = a.ValidMask, bm = b.ValidMask;
return new UInt64Value(av ^ bv, am & bm);
}

public static UInt64Value Not(UInt64Value a) {
return new UInt64Value(~a.Value, a.ValidMask);
}

public static UInt64Value Shl(UInt64Value a, Int32Value b) {
if (b.HasUnknownBits())
return CreateUnknown();
if (b.Value == 0)
return a;
if (b.Value < 0 || b.Value >= sizeof(ulong) * 8)
return CreateUnknown();
int shift = b.Value;
ulong validMask = (a.ValidMask << shift) | (ulong.MaxValue >> (sizeof(ulong) * 8 - shift));
return new UInt64Value(a.Value << shift, validMask);
}

public static UInt64Value Shr(UInt64Value a, Int32Value b) {
if (b.HasUnknownBits())
return CreateUnknown();
if (b.Value == 0)
return a;
if (b.Value < 0 || b.Value >= sizeof(ulong) * 8)
return CreateUnknown();
int shift = b.Value;
ulong validMask = a.ValidMask >> shift;
return new UInt64Value(a.Value >> shift, validMask);
}

public static UInt64Value Shr_Un(UInt64Value a, Int32Value b) {
return Shr(a, b);
}

public static Int32Value Ceq(UInt64Value a, UInt64Value b) {
return Int32Value.Create(CompareEq(a, b));
}

public static Int32Value Cgt(UInt64Value a, UInt64Value b) {
return Int32Value.Create(CompareGt_Un(a, b));
}

public static Int32Value Cgt_Un(UInt64Value a, UInt64Value b) {
return Int32Value.Create(CompareGt_Un(a, b));
}

public static Int32Value Clt(UInt64Value a, UInt64Value b) {
return Int32Value.Create(CompareLt_Un(a, b));
}

public static Int32Value Clt_Un(UInt64Value a, UInt64Value b) {
return Int32Value.Create(CompareLt_Un(a, b));
}

public static Bool3 CompareEq(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return a.Value == b.Value ? Bool3.True : Bool3.False;
if (ReferenceEquals(a, b))
return Bool3.True;
if (((ulong)a.Value & a.ValidMask & b.ValidMask) != ((ulong)b.Value & a.ValidMask & b.ValidMask))
return Bool3.False;
return Bool3.Unknown;
}

public static Bool3 CompareNeq(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return a.Value != b.Value ? Bool3.True : Bool3.False;
if (ReferenceEquals(a, b))
return Bool3.False;
if (((ulong)a.Value & a.ValidMask & b.ValidMask) != ((ulong)b.Value & a.ValidMask & b.ValidMask))
return Bool3.True;
return Bool3.Unknown;
}

public static Bool3 CompareGt_Un(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return a.Value > b.Value ? Bool3.True : Bool3.False;
if (a.HasValue(ulong.MinValue))
return Bool3.False;
if (b.HasValue(ulong.MaxValue))
return Bool3.False;
return Bool3.Unknown;
}

public static Bool3 CompareGe_Un(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return a.Value >= b.Value ? Bool3.True : Bool3.False;
if (a.HasValue(ulong.MaxValue))
return Bool3.True;
if (b.HasValue(ulong.MinValue))
return Bool3.True;
return Bool3.Unknown;
}

public static Bool3 CompareLe_Un(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return a.Value <= b.Value ? Bool3.True : Bool3.False;
if (a.HasValue(ulong.MinValue))
return Bool3.True;
if (b.HasValue(ulong.MaxValue))
return Bool3.True;
return Bool3.Unknown;
}

public static Bool3 CompareLt_Un(UInt64Value a, UInt64Value b) {
if (a.AllBitsValid() && b.AllBitsValid())
return a.Value < b.Value ? Bool3.True : Bool3.False;
if (a.HasValue(ulong.MaxValue))
return Bool3.False;
if (b.HasValue(ulong.MinValue))
return Bool3.False;
return Bool3.Unknown;
}

public static Bool3 CompareTrue(UInt64Value a) {
if (a.AllBitsValid())
return a.Value != 0 ? Bool3.True : Bool3.False;
if (((ulong)a.Value & a.ValidMask) != 0)
return Bool3.True;
return Bool3.Unknown;
}

public static Bool3 CompareFalse(UInt64Value a) {
if (a.AllBitsValid())
return a.Value == 0 ? Bool3.True : Bool3.False;
if (((ulong)a.Value & a.ValidMask) != 0)
return Bool3.False;
return Bool3.Unknown;
}

public override string ToString() {
if (AllBitsValid())
return Value.ToString();
return string.Format("0x{0:X8}UL({1:X8})", Value, ValidMask);
}
}
}
