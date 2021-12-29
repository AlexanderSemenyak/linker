﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Mono.Linker.Tests.Cases.Expectations.Assertions;

namespace Mono.Linker.Tests.Cases.Attributes.StructLayout
{
	class UnusedTypeWithSequentialLayoutIsRemoved
	{
		static void Main ()
		{
		}

		[StructLayout (LayoutKind.Sequential)]
		class B
		{
			int a;
		}
	}
}