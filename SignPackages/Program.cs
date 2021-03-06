﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace SignPackages
{
    class Program
    {
        static void Main(string[] args)
        {

            var currentDirectory = Directory.GetCurrentDirectory();
            while (true)
            {
                if (Directory.Exists(Path.Combine(currentDirectory, "TypeEdgeNuGets")))
                {
                    currentDirectory = Path.Combine(currentDirectory, "TypeEdgeNuGets");
                    break;
                }

                if (Directory.GetParent(currentDirectory) != null)
                    currentDirectory = Directory.GetParent(currentDirectory).FullName;
                else
                {
                    Console.WriteLine("Error finding TypeEdgeNuGets Directory");
                    return;
                }
            }

            var fileTemplate = "";
            using (var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("SignPackages.SignTemplate.json"))
            using (var reader = new StreamReader(stream))
                fileTemplate = reader.ReadToEnd();

            var esrpClient = "";
            foreach (var dir in Directory.GetDirectories(currentDirectory, "EsrpClient.*"))
                esrpClient = dir;
            var dllInputTemplate = File.ReadAllText($"{esrpClient}\\dll_template.json");
            var nugetInputTemplate = File.ReadAllText($"{esrpClient}\\nuget_template.json");
            

            SignPackage("Microsoft.Azure.TypeEdge", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            SignPackage("Microsoft.Azure.TypeEdge.Host", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            SignPackage("Microsoft.Azure.TypeEdge.Proxy", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);


            SignPackage("Microsoft.Azure.TypeEdge.Templates.Emulator", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            SignPackage("Microsoft.Azure.TypeEdge.Templates.Module.VsCode", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            SignPackage("Microsoft.Azure.TypeEdge.Templates.ML", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            SignPackage("Microsoft.Azure.TypeEdge.Templates.ServiceProxy", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            SignPackage("Microsoft.Azure.TypeEdge.Templates.Module", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            SignPackage("Microsoft.Azure.TypeEdge.Templates.Application", currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
        }
        private static void SignPackage(string file,
             string currentDirectory,
            string fileTemplate,
            string dllInputTemplate,
            string nugetInputTemplate,
            string esrpClient)
        {
            var _file = Directory.GetFiles(currentDirectory, $"{file}.?.*.nupkg").OrderByDescending(e =>
            {
                var version = Path.GetFileName(e).Replace($"{file}.", "").Replace(".nupkg", "");

                long multiplier = 100;
                long res = 0;
                foreach (var item in version.Split(".").Reverse())
                {
                    res += Convert.ToInt32(item) * multiplier;
                    multiplier *= multiplier;
                }
                return res;
            }).First();
            Console.WriteLine($"Signing {_file}...");
            if (IsSigned(currentDirectory, _file))
            {
                Console.WriteLine($"File {_file} already signed.");
                return;
            }
            _SignPackage(_file, currentDirectory, fileTemplate, dllInputTemplate, nugetInputTemplate, esrpClient);
            if (!IsSigned(currentDirectory, _file))
                throw new Exception($"Cannot verify {_file}");
        }
        private static bool IsSigned(string folder, string file)
        {
            var command = $@"/C nuget verify -signature -CertificateFingerprint 3F9001EA83C560D712C24CF213C3D312CB3BFF51EE89435D3430BD06B5D0EECE {file}";

            var proc = new Process();
            proc.StartInfo.FileName = "cmd.exe";
            proc.StartInfo.Arguments = command;
            proc.StartInfo.UseShellExecute = true;
            //proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.WorkingDirectory = folder;
            proc.StartInfo.Verb = "runas";
            proc.StartInfo.LoadUserProfile = true;

            proc.Start();

            //Console.WriteLine(proc.StandardOutput.ReadToEnd());

            proc.WaitForExit();

            var res = proc.ExitCode;
            proc.Close();
            return res == 0;
        }

        private static void _SignPackage(string file,
            string currentDirectory,
            string fileTemplate,
            string dllInputTemplate,
            string nugetInputTemplate,
            string esrpClient)
        {
            var folder = $"{currentDirectory}\\Extracted\\{Path.GetFileName(file)}";
            ZipFile.ExtractToDirectory(file, folder, true);

            var files = GenerateSignInput(Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories), fileTemplate);
            var fileInput = dllInputTemplate.Replace("FILES", files);
            var inputFileName = Path.Combine(esrpClient, $"{Path.GetFileName(file)}.json");
            File.WriteAllText(inputFileName, fileInput);

            Sign(esrpClient, Path.GetFileName(inputFileName));

            File.Delete(file);

            ZipFile.CreateFromDirectory(folder, file, CompressionLevel.Optimal, includeBaseDirectory: false);
            Directory.Delete(folder, true);

            files = GenerateSignInput(new[] { file }, fileTemplate);
            fileInput = nugetInputTemplate.Replace("FILES", files);
            inputFileName = Path.Combine(esrpClient, $"{Path.GetFileName(file)}.nuget.json");

            File.WriteAllText(inputFileName, fileInput);

            Sign(esrpClient, Path.GetFileName(inputFileName));
        }

        private static void Sign(string folder, string input)
        {
            var command = $@"/C tools\EsrpClient.exe sign -a auth.json -p policy.json -i {input} -l verbose";

            var proc = new Process();
            proc.StartInfo.FileName = "cmd.exe";
            proc.StartInfo.Arguments = command;
            proc.StartInfo.UseShellExecute = true;
            proc.StartInfo.WorkingDirectory = folder;
            proc.StartInfo.Verb = "runas";
            proc.StartInfo.LoadUserProfile = true;
            proc.Start();
            proc.WaitForExit();
            proc.Close();
        }

        private static string GenerateSignInput(string[] files, string fileTemplate)
        {
            var res = new List<string>();
            res.AddRange(files.Select(file => fileTemplate.Replace("PATH", file.Replace(@"\", @"\\"))));
            return string.Join(", ", res);
        }
    }
}
