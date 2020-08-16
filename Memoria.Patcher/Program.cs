using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using Fossil;
using System.Security.Cryptography;

namespace Memoria.Patcher
{
    static class Program
    {
        static void Main(String[] args)
        {
            try
            {
                if (args.Length > 1 && args[0] == "-update")
                {
                    String launcherProcessPath = args[1];
                    String launcherProcessDirectory = Path.GetDirectoryName(launcherProcessPath);

                    if (args.Length > 2)
                    {
                        Int32 launcherProcessId = Int32.Parse(args[2]);
                        try
                        {
                            Process process = Process.GetProcessById(launcherProcessId);
                            process.Kill();
                            process.WaitForExit();
                        }
                        catch
                        {
                        }

                        Run(new[] {launcherProcessDirectory});

                        String arguments = $"-update \"{launcherProcessPath}\"";
                        foreach (String patcher in args.Skip(3))
                        {
                            Process process = Process.Start(patcher, arguments);
                            process?.WaitForExit();
                        }

                        Process.Start(launcherProcessPath);
                    }
                    else
                    {
                        Run(new[] {launcherProcessDirectory});
                    }

                    Environment.Exit(0);
                }
                else
                {
                    Run(args);
                    Console.WriteLine(Lang.Message.Done.Success);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unexpected error has occurred.");
                Console.WriteLine("---------------------------");
                Console.WriteLine(ex);
                Console.WriteLine("---------------------------");
                Console.WriteLine(Lang.Message.Done.PressEnterToExit);
                Console.ReadLine();
            }

            
        }

        private static void Run(String[] args)
        {
            GameLocationInfo gameLocation = GetGameLocation(args);
            if (gameLocation == null)
            {
                Console.WriteLine();
                Console.WriteLine("{0}.exe <gamePath>", Assembly.GetExecutingAssembly().GetName().Name);
                Console.WriteLine("Press enter to exit...");
                Console.ReadLine();
                Environment.Exit(1);
            }

            String executablePath = Assembly.GetEntryAssembly().Location;
            using (FileStream inputFile = File.OpenRead(executablePath))
            {
                inputFile.Seek(-3 * 8, SeekOrigin.End);
                BinaryReader br = new BinaryReader(inputFile);

                Int64 magicNumber = br.ReadInt64();
                Int64 uncompressedDataSize = br.ReadInt64();
                Int64 compressedDataPosition = br.ReadInt64();
                if (magicNumber != 0x004149524F4D454D) // MEMORIA\0
                    throw new InvalidDataException("Invalid magic number: " + magicNumber);

                inputFile.Position = compressedDataPosition;
                using (ConsoleProgressHandler progressHandler = new ConsoleProgressHandler(uncompressedDataSize))
                using (GZipStream input = new GZipStream(inputFile, CompressionMode.Decompress))
                using (br = new BinaryReader(input))
                {
                    Int64 leftSize = uncompressedDataSize;
                    ExtractFiles(gameLocation, input, br, ref leftSize, progressHandler);
                }
            }
        }

        private static void ExtractFiles(GameLocationInfo gameLocation, GZipStream input, BinaryReader br, ref Int64 leftSize, ConsoleProgressHandler progressHandler)
        {
            Dictionary<Int16, String> pathMap = new Dictionary<Int16, String>(400);
            UInt16 idMask = 1 << 15;

            Byte[] buff = new Byte[64 * 1024];

            while (leftSize > 0)
            {
                Int64 uncompressedSize = br.ReadUInt32();
                DateTime writeTimeUtc = new DateTime(br.ReadInt64(), DateTimeKind.Utc);

                Boolean hasPlatform = false;
                Boolean needPatching = false;
                String[] pathParts = new String[br.ReadByte() + 1];
                pathParts[0] = gameLocation.RootDirectory;
                for (Int32 i = 1; i < pathParts.Length; i++)
                {
                    String part = null;

                    Int16 id = br.ReadInt16();
                    if ((id & idMask) == idMask)
                    {
                        id = (Int16)(id & ~idMask);

                        Int32 bytesNumber = br.ReadByte();
                        Int32 readed = 0;
                        while (bytesNumber > 0)
                        {
                            readed = br.Read(buff, readed, bytesNumber);
                            bytesNumber -= readed;
                        }

                        part = Encoding.UTF8.GetString(buff, 0, readed);
                        pathParts[i] = part;
                        pathMap.Add(id, part);
                    }
                    else
                    {
                        part = pathMap[id];
                        pathParts[i] = part;
                    }
                    if (part == "resources_patch.diff")
                        needPatching = true;
                        

                    if (part == "{PLATFORM}")
                        hasPlatform = true;
                }

                String outputPath = Path.Combine(pathParts);

                if (hasPlatform)
                {                  
                    if (Directory.Exists(gameLocation.ManagedPathX64))
                    {
                        outputPath = outputPath.Replace("{PLATFORM}", "x64");
                        if (needPatching)
                            PatchFile(input, uncompressedSize, writeTimeUtc, progressHandler, outputPath);
                        else
                            ExtractFile(input, uncompressedSize, buff, writeTimeUtc, progressHandler, outputPath);
                    }
                    
                    else
                    {
                        progressHandler.IncrementProcessedSize(uncompressedSize);
                    }
                }
                else
                {
                    ExtractFile(input, uncompressedSize, buff, writeTimeUtc, progressHandler, outputPath);
                }

                leftSize -= uncompressedSize;
            }
        }

        private static void PatchFile(GZipStream input, Int64 uncompressedSize, DateTime writeTimeUtc, ConsoleProgressHandler progressHandler, params String[] outputPaths)
        {
            
            byte[] buffer = new Byte[uncompressedSize];
            List<FileStream> outputs = new List<FileStream>(outputPaths.Length);
            try
            {
                foreach (String outputPath in outputPaths)
                {
                    String patch_outputPath = outputPath.Replace("_patch.diff", ".assets");
                    BackupFile(patch_outputPath);

                }
                Int32 readed = input.Read(buffer, 0, (int)uncompressedSize);
                progressHandler.IncrementProcessedSize(readed);
            }
            finally
            {
                foreach (String outputPath in outputPaths)
                {
                    
                    String patch_outputPath = outputPath.Replace("_patch.diff", ".assets");
                    Console.WriteLine("Patching {0}", patch_outputPath);

                    Boolean canPatch = true;
                    using (var md5 = MD5.Create())
                    {
                        using (var stream = File.OpenRead(patch_outputPath.Replace(".assets", ".bak")))
                        {
                            var hash = md5.ComputeHash(stream);
                            var hash_str = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                            if(hash_str != "4687cc936767974bc9933fb7c12667f7")
                            {
                                canPatch = false;
                                Console.WriteLine("Cannot patch :Invalid hash for file: {0}", patch_outputPath.Replace(".assets", ".bak"));
                            }
                        }
                    }
                    if(canPatch == true)
                    { 
                        byte[] right = File.ReadAllBytes(patch_outputPath.Replace(".assets", ".bak"));
                        byte[] outF = Fossil.Delta.Apply(right, buffer);
                        File.WriteAllBytes(patch_outputPath, outF);
                    }
                    else
                    {
                        if (!File.Exists(patch_outputPath))
                            File.Copy(patch_outputPath.Replace(".assets", ".bak"), patch_outputPath);
                    }
                }
            }

            foreach (String outputPath in outputPaths)
            {
                String patch_outputPath = outputPath.Replace("_patch.diff", ".assets");
                File.SetLastWriteTimeUtc(patch_outputPath, writeTimeUtc);
             }
        }


        private static void ExtractFile(GZipStream input, Int64 uncompressedSize, Byte[] buff, DateTime writeTimeUtc, ConsoleProgressHandler progressHandler, params String[] outputPaths)
        {
            List<FileStream> outputs = new List<FileStream>(outputPaths.Length);
            try
            {
                foreach (String outputPath in outputPaths)
                    outputs.Add(OverwriteFile(outputPath));

                while (uncompressedSize > 0)
                {
                    Int32 readed = input.Read(buff, 0, (Int32)Math.Min(uncompressedSize, buff.Length));
                    uncompressedSize -= readed;

                    foreach (FileStream output in outputs)
                        output.Write(buff, 0, readed);

                    progressHandler.IncrementProcessedSize(readed);
                }
            }
            finally
            {
                foreach (FileStream output in outputs)
                    output.Dispose();
            }

            foreach (String outputPath in outputPaths)
                File.SetLastWriteTimeUtc(outputPath, writeTimeUtc);
        }



        private static readonly HashSet<String> _filesForBackup = new HashSet<String>(StringComparer.OrdinalIgnoreCase) {".exe", ".dll", ".assets"};


        private static void BackupFile(String outputPath)
        {
            if (File.Exists(outputPath))
            {
                String extension = Path.GetExtension(outputPath);
                if (_filesForBackup.Contains(extension))
                {
                      String backupPath = Path.ChangeExtension(outputPath, ".bak");
                    if (!File.Exists(backupPath))
                        File.Move(outputPath, backupPath);
                }
            }
        }

        private static FileStream OverwriteFile(String outputPath)
        {
            if (File.Exists(outputPath))
            {
                String extension = Path.GetExtension(outputPath);
                if (_filesForBackup.Contains(extension))
                {
                    String backupPath = Path.ChangeExtension(outputPath, ".bak");
                    if (!File.Exists(backupPath))
                        File.Move(outputPath, backupPath);
                }
            }
            else
            {
                String directoryName = Path.GetDirectoryName(outputPath);
                if (directoryName != null)
                    Directory.CreateDirectory(directoryName);
            }

            return File.Create(outputPath);
        }



        private static GameLocationInfo GetGameLocation(String[] args)
        {
            try
            {
                GameLocationInfo result;
                if (args == null || args.Length == 0)
                {
                    if (File.Exists(GameLocationInfo.LauncherName))
                    {
                        result = new GameLocationInfo(Environment.CurrentDirectory);
                        result.Validate();
                    }
                    else
                    {
                        result = GameLocationSteamRegistryProvider.TryLoad();
                    }
                }
                else
                {
                    result = new GameLocationInfo(args[0]);
                    result.Validate();
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to get a game location.");
                Console.WriteLine("---------------------------");
                Console.WriteLine(ex);
                Console.WriteLine("---------------------------");
                return null;
            }
        }
    }
}