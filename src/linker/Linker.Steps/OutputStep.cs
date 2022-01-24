// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// � .NET Foundation and contributions

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using ILLink.Shared;
using Mono.Cecil;

namespace Mono.Linker.Steps
{

	public class OutputStep : BaseStep
	{
		private Dictionary<UInt16, TargetArchitecture>? architectureMap;

		private enum NativeOSOverride
		{
			Apple = 0x4644,
			FreeBSD = 0xadc4,
			Linux = 0x7b79,
			NetBSD = 0x1993,
			Default = 0
		}

		readonly List<string> assembliesWritten;

		public OutputStep ()
		{
			assembliesWritten = new List<string> ();
		}

		TargetArchitecture CalculateArchitecture (TargetArchitecture readyToRunArch)
		{
			if (architectureMap == null) {
				architectureMap = new Dictionary<UInt16, TargetArchitecture> ();
				foreach (var os in Enum.GetValues (typeof (NativeOSOverride))) {
					ushort osVal = (ushort) (NativeOSOverride) os;
					foreach (var arch in Enum.GetValues (typeof (TargetArchitecture))) {
						ushort archVal = (ushort) (TargetArchitecture) arch;
						architectureMap.Add ((ushort) (archVal ^ osVal), (TargetArchitecture) arch);
					}
				}
			}

			if (architectureMap.TryGetValue ((ushort) readyToRunArch, out TargetArchitecture pureILArch)) {
				return pureILArch;
			}
			throw new BadImageFormatException ("unrecognized module attributes");
		}

		protected override bool ConditionToProcess ()
		{
			return Context.ErrorsCount == 0;
		}

		protected override void Process ()
		{
			CheckOutputDirectory ();
			OutputPInvokes ();
			Tracer.Finish ();
		}

		protected override void EndProcess ()
		{
			if (Context.AssemblyListFile != null) {
				using (var w = File.CreateText (Context.AssemblyListFile)) {
					w.WriteLine ("[" + String.Join (", ", assembliesWritten.Select (a => "\"" + a + "\"").ToArray ()) + "]");
				}
			}
		}

		void CheckOutputDirectory ()
		{
			if (Directory.Exists (Context.OutputDirectory))
				return;

			Directory.CreateDirectory (Context.OutputDirectory);
		}

		protected override void ProcessAssembly (AssemblyDefinition assembly)
		{
			OutputAssembly (assembly);
		}

		protected void WriteAssembly (AssemblyDefinition assembly, string directory)
		{
			WriteAssembly (assembly, directory, SaveSymbols (assembly));
		}

		protected virtual void WriteAssembly (AssemblyDefinition assembly, string directory, WriterParameters writerParameters)
		{
			foreach (var module in assembly.Modules) {
				// Write back pure IL even for crossgen-ed assemblies
				if (module.IsCrossgened ()) {
					module.Attributes |= ModuleAttributes.ILOnly;
					module.Attributes ^= ModuleAttributes.ILLibrary;
					module.Architecture = CalculateArchitecture (module.Architecture);
				}
			}

			string outputName = GetAssemblyFileName (assembly, directory);
			try {
				assembly.Write (outputName, writerParameters);
			} catch (Exception e) {
				throw new LinkerFatalErrorException (MessageContainer.CreateErrorMessage (null, DiagnosticId.FailedToWriteOutput, outputName), e);
			}
		}

		void OutputAssembly (AssemblyDefinition assembly)
		{
			string directory = Context.OutputDirectory;

			CopyConfigFileIfNeeded (assembly, directory);

			var action = Annotations.GetAction (assembly);
			Context.LogMessage ($"Output action: '{action,8}' assembly: '{assembly}'.");

			switch (action) {
			case AssemblyAction.Save:
			case AssemblyAction.Link:
			case AssemblyAction.AddBypassNGen:
				WriteAssembly (assembly, directory);
				CopySatelliteAssembliesIfNeeded (assembly, directory);
				assembliesWritten.Add (GetOriginalAssemblyFileInfo (assembly).Name);
				break;
			case AssemblyAction.Copy:
				CloseSymbols (assembly);
				CopyAssembly (assembly, directory);
				CopySatelliteAssembliesIfNeeded (assembly, directory);
				assembliesWritten.Add (GetOriginalAssemblyFileInfo (assembly).Name);
				break;
			case AssemblyAction.Delete:
				CloseSymbols (assembly);
				DeleteAssembly (assembly, directory);
				break;
			default:
				CloseSymbols (assembly);
				break;
			}
		}

		private void OutputPInvokes ()
		{
			if (Context.PInvokesListFile == null)
				return;

			using (var fs = File.Open (Path.Combine (Context.OutputDirectory, Context.PInvokesListFile), FileMode.Create)) {
				var values = Context.PInvokes.Distinct ().OrderBy (l => l);
				var jsonSerializer = new DataContractJsonSerializer (typeof (List<PInvokeInfo>));
				jsonSerializer.WriteObject (fs, values);
			}
		}

		protected virtual void DeleteAssembly (AssemblyDefinition assembly, string directory)
		{
			var target = GetAssemblyFileName (assembly, directory);
			if (File.Exists (target)) {
				File.Delete (target);
				File.Delete (target + ".mdb");
				File.Delete (Path.ChangeExtension (target, "pdb"));
				File.Delete (GetConfigFile (target));
			}
		}

		void CloseSymbols (AssemblyDefinition assembly)
		{
			Annotations.CloseSymbolReader (assembly);
		}

		WriterParameters SaveSymbols (AssemblyDefinition assembly)
		{
			var parameters = new WriterParameters {
				DeterministicMvid = Context.DeterministicOutput
			};

			if (!Context.LinkSymbols)
				return parameters;

			if (!assembly.MainModule.HasSymbols)
				return parameters;

			// Use a string check to avoid a hard dependency on Mono.Cecil.Pdb
			if (Environment.OSVersion.Platform != PlatformID.Win32NT && assembly.MainModule.SymbolReader.GetType ().FullName == "Mono.Cecil.Pdb.NativePdbReader")
				return parameters;

			parameters.WriteSymbols = true;
			return parameters;
		}


		void CopySatelliteAssembliesIfNeeded (AssemblyDefinition assembly, string directory)
		{
			if (!Annotations.ProcessSatelliteAssemblies)
				return;

			FileInfo original = GetOriginalAssemblyFileInfo (assembly);
			string resourceFile = GetAssemblyResourceFileName (original.FullName);

			foreach (var subDirectory in Directory.EnumerateDirectories (original.DirectoryName!)) {
				var satelliteAssembly = Path.Combine (subDirectory, resourceFile);
				if (!File.Exists (satelliteAssembly))
					continue;

				string cultureName = subDirectory.Substring (subDirectory.LastIndexOf (Path.DirectorySeparatorChar) + 1);
				string culturePath = Path.Combine (directory, cultureName);

				Directory.CreateDirectory (culturePath);
				File.Copy (satelliteAssembly, Path.Combine (culturePath, resourceFile), true);
			}
		}

		void CopyConfigFileIfNeeded (AssemblyDefinition assembly, string directory)
		{
			string config = GetConfigFile (GetOriginalAssemblyFileInfo (assembly).FullName);
			if (!File.Exists (config))
				return;

			string target = Path.GetFullPath (GetConfigFile (GetAssemblyFileName (assembly, directory)));

			if (config == target)
				return;

			File.Copy (config, GetConfigFile (GetAssemblyFileName (assembly, directory)), true);
		}

		static string GetAssemblyResourceFileName (string assembly)
		{
			return Path.GetFileNameWithoutExtension (assembly) + ".resources.dll";
		}

		static string GetConfigFile (string assembly)
		{
			return assembly + ".config";
		}

		FileInfo GetOriginalAssemblyFileInfo (AssemblyDefinition assembly)
		{
			return new FileInfo (Context.GetAssemblyLocation (assembly));
		}

		protected virtual void CopyAssembly (AssemblyDefinition assembly, string directory)
		{
			FileInfo fi = GetOriginalAssemblyFileInfo (assembly);
			string target = Path.GetFullPath (Path.Combine (directory, fi.Name));
			string source = fi.FullName;

			if (source == target)
				return;

			File.Copy (source, target, true);
			if (!Context.LinkSymbols)
				return;

			var mdb = source + ".mdb";
			if (File.Exists (mdb))
				File.Copy (mdb, target + ".mdb", true);

			var pdb = Path.ChangeExtension (source, "pdb");
			if (File.Exists (pdb))
				File.Copy (pdb, Path.ChangeExtension (target, "pdb"), true);
		}

		protected virtual string GetAssemblyFileName (AssemblyDefinition assembly, string directory)
		{
			string file = GetOriginalAssemblyFileInfo (assembly).Name;
			return Path.Combine (directory, file);
		}
	}
}
