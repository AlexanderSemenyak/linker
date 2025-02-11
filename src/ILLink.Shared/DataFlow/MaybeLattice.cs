// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
using System;

namespace ILLink.Shared.DataFlow
{
	// Wrapper for Nullable<T> which implements IEquatable so that this may
	// be used as a lattice value. Nullable types can't satisfy interface constraints;
	// see for example https://docs.microsoft.com/dotnet/csharp/misc/cs0313.
	public struct Maybe<T> : IEquatable<Maybe<T>>
		where T : struct, IEquatable<T>
	{
		public T? MaybeValue;
		public Maybe (T value) => MaybeValue = value;
		public bool Equals (Maybe<T> other) => MaybeValue?.Equals (other.MaybeValue) ?? other.MaybeValue == null;
		public override bool Equals (object? obj) => obj is Maybe<T> other && Equals (other);
		public override int GetHashCode () => MaybeValue?.GetHashCode () ?? 0;
		public Maybe<T> Clone ()
		{
			if (MaybeValue is not T value)
				return default;
			if (value is IDeepCopyValue<T> copyValue)
				return new (copyValue.DeepCopy ());
			return new (value);
		}
	}

	public struct MaybeLattice<T, TValueLattice> : ILattice<Maybe<T>>
		where T : struct, IEquatable<T>
		where TValueLattice : ILattice<T>
	{
		public readonly TValueLattice ValueLattice;
		public MaybeLattice (TValueLattice valueLattice)
		{
			ValueLattice = valueLattice;
			Top = default;
		}
		public Maybe<T> Top { get; }
		public Maybe<T> Meet (Maybe<T> left, Maybe<T> right)
		{
			if (left.MaybeValue is not T leftValue)
				return right.Clone ();
			if (right.MaybeValue is not T rightValue)
				return left.Clone ();
			return new Maybe<T> (ValueLattice.Meet (leftValue, rightValue));
		}
	}
}