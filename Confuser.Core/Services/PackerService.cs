﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confuser.Core.Project;
using dnlib.DotNet;
using Microsoft.Extensions.DependencyInjection;

namespace Confuser.Core.Services {
	internal sealed class PackerService : IPackerService {
		private readonly ILoggingService loggingService;

		public PackerService(IServiceProvider provider) => 
			loggingService = provider.GetRequiredService<ILoggingService>();

		public void ProtectStub(IConfuserContext context1, string fileName, byte[] module, StrongNameKey snKey, IProtection prot, CancellationToken token) {
			var context = (ConfuserContext)context1;
			string tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
			string outDir = Path.Combine(tmpDir, Path.GetRandomFileName());
			Directory.CreateDirectory(tmpDir);

			for (int i = 0; i < context.OutputModules.Count; i++) {
				string path = Path.GetFullPath(Path.Combine(tmpDir, context.OutputPaths[i]));
				var dir = Path.GetDirectoryName(path);
				if (!Directory.Exists(dir))
					Directory.CreateDirectory(dir);
				File.WriteAllBytes(path, context.OutputModules[i]);
			}
			File.WriteAllBytes(Path.Combine(tmpDir, fileName), module);

			var proj = new ConfuserProject();
			proj.Seed = context.Project.Seed;
			foreach (Rule rule in context.Project.Rules)
				proj.Rules.Add(rule);
			proj.Add(new ProjectModule {
				Path = fileName
			});
			proj.BaseDirectory = tmpDir;
			proj.OutputDirectory = outDir;
			foreach (var path in context.Project.ProbePaths)
				proj.ProbePaths.Add(path);
			proj.ProbePaths.Add(context.Project.BaseDirectory);

			PluginDiscovery discovery = null;
			if (prot != null) {
				var rule = new Rule {
					Preset = ProtectionPreset.None,
					Inherit = true,
					Pattern = "true"
				};
				rule.Add(new SettingItem<IProtection> {
					Id = prot.Id,
					Action = SettingItemAction.Add
				});
				proj.Rules.Add(rule);
				discovery = new PackerDiscovery(prot);
			}

			var logger = loggingService.GetLogger("packer");

			try {
				ConfuserEngine.Run(new ConfuserParameters {
					Logger = new PackerLogger(logger),
					PluginDiscovery = discovery,
					Marker = new PackerMarker(snKey),
					Project = proj,
					PackerInitiated = true
				}, token).Wait();
			}
			catch (AggregateException ex) {
				logger.Error("Failed to protect packer stub.");
				throw new ConfuserException(ex);
			}

			context.OutputModules = ImmutableArray.Create<byte[]>(File.ReadAllBytes(Path.Combine(outDir, fileName)));
			context.OutputPaths = ImmutableArray.Create(fileName);

		}

		private sealed class PackerLogger : ILogger {
			readonly ILogger baseLogger;

			public PackerLogger(ILogger baseLogger) => this.baseLogger = baseLogger;

			void ILogger.Debug(string msg) => baseLogger.Debug(msg);

			void ILogger.DebugFormat(string format, params object[] args) => baseLogger.DebugFormat(format, args);

			void ILogger.Info(string msg) => baseLogger.Info(msg);

			void ILogger.InfoFormat(string format, params object[] args) => baseLogger.InfoFormat(format, args);

			void ILogger.Warn(string msg) => baseLogger.Warn(msg);

			void ILogger.WarnFormat(string format, params object[] args) => baseLogger.WarnFormat(format, args);

			void ILogger.WarnException(string msg, Exception ex) => baseLogger.WarnException(msg, ex);

			void ILogger.Error(string msg) => baseLogger.Error(msg);

			void ILogger.ErrorFormat(string format, params object[] args) => baseLogger.ErrorFormat(format, args);

			void ILogger.ErrorException(string msg, Exception ex) => baseLogger.ErrorException(msg, ex);

			void ILogger.Progress(int progress, int overall) {
				baseLogger.Progress(progress, overall);
			}

			void ILogger.EndProgress() => baseLogger.EndProgress();

			void ILogger.Finish(bool successful) {
				if (!successful) throw new ConfuserException(null);
				baseLogger.Info("Finish protecting packer stub.");
			}
		}
		
		private sealed class PackerMarker : Marker {
			readonly StrongNameKey snKey;

			public PackerMarker(StrongNameKey snKey) => this.snKey = snKey;

			protected internal override MarkerResult MarkProject(ConfuserProject proj, ConfuserContext context, CancellationToken token) {
				var result = base.MarkProject(proj, context, token);
				foreach (var module in result.Modules)
					context.Annotations.Set(module, SNKey, snKey);
				return result;
			}
		}

		internal class PackerDiscovery : PluginDiscovery {
			private readonly IProtection prot;

			public PackerDiscovery(IProtection prot) {
				this.prot = prot;
			}

			protected override AggregateCatalog GetAdditionalPlugIns(ConfuserProject project, ILogger logger) {
				var catalog = base.GetAdditionalPlugIns(project, logger);
				if (prot == null) return catalog;

				return new AggregateCatalog(
					catalog,
					new PackerCompositionCatalog(prot));
			}
		}

		private sealed class PackerCompositionCatalog : ComposablePartCatalog {
			private readonly ComposablePartDefinition partDef;

			public PackerCompositionCatalog(IProtection prot) => partDef = new PackerComposablePartDefinition(prot);

			public override IEnumerable<Tuple<ComposablePartDefinition, ExportDefinition>> GetExports(ImportDefinition definition) {
				if (definition == null) throw new ArgumentNullException(nameof(definition));

				return partDef.ExportDefinitions
					.Where(definition.IsConstraintSatisfiedBy)
					.Select(def => Tuple.Create(partDef, def));
			}
		}

		private sealed class PackerComposablePartDefinition : ComposablePartDefinition {
			private readonly ComposablePart part;

			public PackerComposablePartDefinition(IProtection prot) => part = new PackerComposablePart(prot);

			public override IEnumerable<ExportDefinition> ExportDefinitions => part.ExportDefinitions;

			public override IEnumerable<ImportDefinition> ImportDefinitions => part.ImportDefinitions;

			public override ComposablePart CreatePart() => part;
		}

		private sealed class PackerComposablePart : ComposablePart {
			private readonly IProtection prot;
			private readonly ExportDefinition exportDef;

			public PackerComposablePart(IProtection prot) {
				this.prot = prot ?? throw new ArgumentNullException(nameof(prot));
				exportDef = new ExportDefinition(typeof(IProtection).FullName,
					ImmutableDictionary.Create<string, object>().Add("ExportTypeIdentity", typeof(IProtection).FullName));
			}

			public override IEnumerable<ExportDefinition> ExportDefinitions => ImmutableArray.Create(exportDef);

			public override IEnumerable<ImportDefinition> ImportDefinitions => Enumerable.Empty<ImportDefinition>();

			public override object GetExportedValue(ExportDefinition definition) => prot;

			public override void SetImport(ImportDefinition definition, IEnumerable<Export> exports) {
			}
		}
	}
}
