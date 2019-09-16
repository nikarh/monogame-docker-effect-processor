using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;

namespace Processor {
    [ContentProcessor(DisplayName = "Cross-platform Effect Processor")]
    public class DockerEffectProcessor : EffectProcessor {
        [Description("Docker image with wine and monogame")]
        [DefaultValue("nikarh/mgcb-fx")]
        public string DockerImage { get; set; } = "nikarh/mgcb-fx";

        private static readonly List<String> openglPlatforms = new List<String> {
            "DesktopGL", "Android", "iOS", "tvOS", "OUYA"
        };

        public override CompiledEffectContent Process(EffectContent input, ContentProcessorContext context) {
            if (Environment.OSVersion.Platform != PlatformID.Unix) {
                return base.Process(input, context);
            }

            var code = ExpandIncludes(input);
            var platform = context.TargetPlatform;
            var buf = RunMGCB(code, DockerImage, platform.ToString(), out var error);

            if (!string.IsNullOrEmpty(error))
                throw new Exception(error);
            if (buf == null || buf.Length == 0)
                throw new Exception("There was an error compiling the effect");

            return new CompiledEffectContent(buf);
        }

        string ExpandIncludes(EffectContent input) {
            var sb = new StringBuilder();
            var root = Path.GetDirectoryName(input.Identity.SourceFilename);
            Include(sb, input.EffectCode.Split('\n'), root);

            return sb.ToString();
        }

        [SuppressMessage("ReSharper", "StringIndexOfIsCultureSpecific.1")]
        [SuppressMessage("ReSharper", "StringIndexOfIsCultureSpecific.2")]
        void Include(StringBuilder sb, string[] lines, string root) {
            foreach (var line in lines) {
                if (line.StartsWith("//")) continue;
                if (line.StartsWith("#include")) {
                    var startIndex = line.IndexOf("\"") + 1;
                    var file = line.Substring(startIndex, line.IndexOf("\"", startIndex) - startIndex);

                    var fullPath = Path.Combine(root, file);
                    Include(sb, File.ReadAllLines(fullPath), Path.GetDirectoryName(fullPath));
                } else {
                    sb.AppendLine(line.Trim());
                }
            }
        }

        byte[] RunMGCB(string code, string image, string platform, out string error) {
            error = string.Empty;

            var profile = openglPlatforms.Contains(platform) ? "OpenGL" : "DirectX_11";
            var tempFile = Path.GetTempFileName();
            var tempPath = Path.GetFileName(Path.ChangeExtension(tempFile, ".fx"));
            File.Delete(tempFile);

            var xnb = Path.ChangeExtension(tempPath, ".mgfx");
            var tempOutput = Path.GetTempPath();
            File.WriteAllText(Path.Combine(tempOutput, tempPath), code);

            try {
                var proc = new Process {
                    StartInfo = {
                        FileName = "docker",
                        Arguments =
                            $"run --rm -v /tmp:/tmp {image} '\\\\tmp\\\\{tempPath}' '\\\\tmp\\\\{xnb}' /Profile:{profile}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true,
                        RedirectStandardError = true
                    }
                };

                var stdoutCompleted = new ManualResetEvent(false);
                proc.Start();

                // Stdout
                var response = new StringBuilder();
                while (!proc.StandardOutput.EndOfStream) {
                    response.AppendLine(proc.StandardOutput.ReadLine());
                }

                // Stderr
                var responseErr = new StringBuilder();
                while (!proc.StandardError.EndOfStream) {
                    responseErr.AppendLine(proc.StandardError.ReadLine());
                }

                if (response.ToString().Contains("Compiled")) {
                    stdoutCompleted.Set();
                } else {
                    error = responseErr.ToString();
                    throw new Exception(error);
                }

                try {
                    proc.WaitForExit();
                    if (File.Exists(Path.Combine(tempOutput, xnb))) {
                        return File.ReadAllBytes(Path.Combine(tempOutput, xnb));
                    }
                } catch (Exception ex) {
                    error = ex.ToString();
                    throw new Exception(error);
                }

                if (proc.ExitCode != 0) {
                    throw new InvalidContentException();
                }
            } finally {
                File.Delete(Path.Combine(tempOutput, tempPath));
                File.Delete(Path.Combine(tempOutput, xnb));
            }

            return new byte[0];
        }
    }
}
