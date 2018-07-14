﻿using System;
using System.Collections.Generic;
using Confuser.Core;
using Confuser.Core.Services;
using Confuser.Renamer.Services;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Microsoft.Extensions.DependencyInjection;

namespace Confuser.Renamer.Analyzers {
	public class WinFormsAnalyzer : IRenamer {
		Dictionary<string, List<PropertyDef>> properties = new Dictionary<string, List<PropertyDef>>();

		public void Analyze(IConfuserContext context, INameService service, IProtectionParameters parameters, IDnlibDef def) {
			if (def is ModuleDef) {
				foreach (var type in ((ModuleDef)def).GetTypes())
					foreach (var prop in type.Properties)
						properties.AddListEntry(prop.Name, prop);
				return;
			}

			var method = def as MethodDef;
			if (method == null || !method.HasBody)
				return;

			AnalyzeMethod(context, service, method);
		}

		void AnalyzeMethod(IConfuserContext context, INameService service, MethodDef method) {
			var binding = new List<Tuple<bool, Instruction>>();
			foreach (Instruction instr in method.Body.Instructions) {
				if ((instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt)) {
					var target = (IMethod)instr.Operand;

					if ((target.DeclaringType.FullName == "System.Windows.Forms.ControlBindingsCollection" ||
					     target.DeclaringType.FullName == "System.Windows.Forms.BindingsCollection") &&
					    target.Name == "Add" && target.MethodSig.Params.Count != 1) {
						binding.Add(Tuple.Create(true, instr));
					}
					else if (target.DeclaringType.FullName == "System.Windows.Forms.Binding" &&
					         target.Name.String == ".ctor") {
						binding.Add(Tuple.Create(false, instr));
					}
				}
			}

			if (binding.Count == 0)
				return;

			var logger = context.Registry.GetRequiredService<ILoggingService>().GetLogger("naming");

			var traceSrv = context.Registry.GetRequiredService<ITraceService>();
			var trace = traceSrv.Trace(method);

			bool erred = false;
			foreach (var instrInfo in binding) {
				int[] args = trace.TraceArguments(instrInfo.Item2);
				if (args == null) {
					if (!erred)
						logger.WarnFormat("Failed to extract binding property name in '{0}'.", method.FullName);
					erred = true;
					continue;
				}

				Instruction propertyName = method.Body.Instructions[args[0 + (instrInfo.Item1 ? 1 : 0)]];
				if (propertyName.OpCode.Code != Code.Ldstr) {
					if (!erred)
						logger.WarnFormat("Failed to extract binding property name in '{0}'.", method.FullName);
					erred = true;
				}
				else {
					List<PropertyDef> props;
					if (!properties.TryGetValue((string)propertyName.Operand, out props)) {
						if (!erred)
							logger.WarnFormat("Failed to extract target property in '{0}'.", method.FullName);
						erred = true;
					}
					else {
						foreach (var property in props)
							service.SetCanRename(context, property, false);
					}
				}

				Instruction dataMember = method.Body.Instructions[args[2 + (instrInfo.Item1 ? 1 : 0)]];
				if (dataMember.OpCode.Code != Code.Ldstr) {
					if (!erred)
						logger.WarnFormat("Failed to extract binding property name in '{0}'.", method.FullName);
					erred = true;
				}
				else {
					List<PropertyDef> props;
					if (!properties.TryGetValue((string)dataMember.Operand, out props)) {
						if (!erred)
							logger.WarnFormat("Failed to extract target property in '{0}'.", method.FullName);
						erred = true;
					}
					else {
						foreach (var property in props)
							service.SetCanRename(context, property, false);
					}
				}
			}
		}


		public void PreRename(IConfuserContext context, INameService service, IProtectionParameters parameters, IDnlibDef def) {
			//
		}

		public void PostRename(IConfuserContext context, INameService service, IProtectionParameters parameters, IDnlibDef def) {
			//
		}
	}
}
