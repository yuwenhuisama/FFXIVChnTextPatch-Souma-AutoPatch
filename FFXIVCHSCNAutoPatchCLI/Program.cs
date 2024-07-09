﻿using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Downloader;

internal class Config
{
    [JsonPropertyName("game_root_path")]
    public string? GameRootPath { get; set; }

    [JsonPropertyName("update_page_url")]
    public string? UpdateUrl { get; set; }

    [JsonPropertyName("update_version")]
    public int? UpdateVersion { get; set; }
}

internal class RemoteUpdateInfo
{
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("update_package_url")]
    public string? UpdatePackageUrl { get; set; }

    [JsonPropertyName("update_package_md5")]
    public string? UpdatePackageMd5 { get; set; }

    [JsonPropertyName("recover_package_url")]
    public string? RecoverPackageUrl { get; set; }

    [JsonPropertyName("recover_package_md5")]
    public string? RecoverPackageMd5 { get; set; }
}

internal class Program
{
    const string CacheDir = "./.cache";
    const string DefaultRemoteUpdateUrl = "";

    public static void Main(string[] args)
    {
        Console.WriteLine("欢迎使用 FFXIV CNS Auto Patch 工具。输入 0 进入更新模式，输入 1 进入恢复模式。");

        var complete = false;

        try
        {
            do
            {
                var mode = Console.ReadLine();
                switch (mode)
                {
                    case "0":
                        ApplyUpdate(false);
                        complete = true;
                        break;
                    case "1":
                        ApplyUpdate(true);
                        complete = true;
                        break;
                    default:
                        Console.WriteLine("输入无效，请重新输入。");
                        complete = false;
                        continue;
                }
            }
            while (!complete);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
        finally
        {
            Console.WriteLine("按任意键退出。");
            Console.ReadKey();   
        }
    }

    private static void ApplyUpdate(bool recoverMode)
    {
        Console.WriteLine("读取设定中。");

        var config = EnsureUpdateProperties();
        Console.WriteLine($"开始从远端获取更新信息: {config.UpdateUrl}");

        using var client = new HttpClient();
        var response = client.GetAsync(config.UpdateUrl).Result;
        var jsonString = response.Content.ReadAsStringAsync().Result;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var remoteUpdateInfo = JsonSerializer.Deserialize<RemoteUpdateInfo>(jsonString, options);

        if (remoteUpdateInfo?.Version is null
            || string.IsNullOrEmpty(remoteUpdateInfo.UpdatePackageUrl?.Trim())
            || string.IsNullOrEmpty(remoteUpdateInfo.UpdatePackageMd5?.Trim())
            || string.IsNullOrEmpty(remoteUpdateInfo.RecoverPackageMd5?.Trim())
            || string.IsNullOrEmpty(remoteUpdateInfo.RecoverPackageUrl?.Trim()))
        {
            Console.WriteLine("未能获取到有效的更新信息，请检查更新页面的 URL 是否正确。");
            throw new Exception("未能获取到有效的更新信息。");
        }

        var urlToDownload = recoverMode ? remoteUpdateInfo.RecoverPackageUrl : remoteUpdateInfo.UpdatePackageUrl;
        var md5ToCheck = recoverMode ? remoteUpdateInfo.RecoverPackageMd5 : remoteUpdateInfo.UpdatePackageMd5;
        md5ToCheck = md5ToCheck.ToUpper();

        if (recoverMode)
        {
            Console.WriteLine("进入恢复模式，将下载恢复包。");
        }
        else if (config.UpdateVersion < remoteUpdateInfo.Version)
        {
            Console.WriteLine($"发现新版本: {remoteUpdateInfo.Version}，开始下载更新包，请耐心等待。");
        }
        else
        {
            Console.WriteLine("当前版本已是最新版本，无需更新。");
            return;
        }

        if (!Directory.Exists(CacheDir))
        {
            Directory.CreateDirectory(CacheDir);
        }

        var zipFilePath = recoverMode ?
            Path.Combine(CacheDir, $"recover.zip") :
            Path.Combine(CacheDir, $"update_{remoteUpdateInfo.Version}.zip");

        bool shouldDownload = true;
        if (File.Exists(zipFilePath))
        {
            Console.WriteLine("发现缓存的同名更新包，检查 MD5 是否正常。");

            var md5 = GetMd5OfFile(zipFilePath);

            Console.WriteLine($"Expected MD5: {md5ToCheck}");
            Console.WriteLine($"Real     MD5: {md5}");

            if (md5 == md5ToCheck)
            {
                Console.WriteLine("缓存的更新包 MD5 正常，跳过下载。");
                shouldDownload = false;
            }
            else
            {
                Console.WriteLine("缓存的更新包 MD5 不正常，删除缓存文件。");
                File.Delete(zipFilePath);
            }
        }

        if (shouldDownload)
        {
            var downloadOpt = new DownloadConfiguration()
            {
                ChunkCount = 8, // file parts to download, the default value is 1
                ParallelDownload = true // download parts of the file as parallel or not. The default value is false
            };
            var downloader = new DownloadService(downloadOpt);
            downloader.DownloadProgressChanged += (sender, args) =>
            {
                var current = args.ReceivedBytesSize;
                var total = args.TotalBytesToReceive;

                double number = (double)current / total;
                string percentageString = $"{number * 100:F2}%";
                Console.Write("\r{0}   ",
                    $"Download progress: current {current / 1024.0 / 1024.0:F2} mb, total {total / 1024.0 / 1024.0:F2} mb, {percentageString}");
            };

            downloader.DownloadFileTaskAsync(urlToDownload, zipFilePath).Wait();

            Console.WriteLine();
            Console.WriteLine("更新包下载完成，开始校验MD5。");

            var hashString = GetMd5OfFile(zipFilePath);

            Console.WriteLine($"Expected MD5: {md5ToCheck}");
            Console.WriteLine($"Real     MD5: {hashString}");

            if (hashString != md5ToCheck)
            {
                Console.WriteLine("MD5 校验失败，停止解压，请重新启动本程序。");
                throw new Exception("MD5 校验失败。");
            }
        }

        var extractPath = Path.Combine(config.GameRootPath!, "game", "sqpack", "ffxiv");
        ZipFile.ExtractToDirectory(zipFilePath, extractPath, true);

        Console.WriteLine("解压完成，开始清理更新包。");
        File.Delete(zipFilePath);

        Console.WriteLine("更新 config.json。");
        config.UpdateVersion = recoverMode ? -1 : remoteUpdateInfo.Version;
        UpdateConfig(config);

        Console.WriteLine("更新完成，重启游戏客户端以应用更新。");
    }

    private static string GetMd5OfFile(string zipFilePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(zipFilePath);
        var hash = md5.ComputeHash(stream);
        StringBuilder sb = new StringBuilder();
        foreach (var t in hash)
        {
            sb.Append(t.ToString("X2"));
        }
        var hashString = sb.ToString().ToUpper();
        return hashString;
    }

    static Config EnsureUpdateProperties()
    {
        var config = GetConfig();

        bool shouldUpdateConf = false;

        if (string.IsNullOrEmpty(config.GameRootPath?.Trim()))
        {
            Console.WriteLine("检测到未设置游戏根目录，请输入游戏根目录全路径并回车进行设置。");

            bool complete = false;
            string? tryPath;

            do
            {
                tryPath = Console.ReadLine();
                if (string.IsNullOrEmpty(tryPath))
                {
                    continue;
                }

                var dir = Path.Combine(tryPath, "game", "sqpack", "ffxiv");
                if (!Directory.Exists(dir))
                {
                    Console.WriteLine($"未找到指定目录 {dir}，请重新输入游戏根目录全路径并回车进行设置。");
                    continue;
                }

                complete = true;
            }
            while (!complete);

            Console.WriteLine("设定游戏根目录成功。");
            config.GameRootPath = tryPath;
            shouldUpdateConf = true;
        }

        if (string.IsNullOrEmpty(config.UpdateUrl?.Trim()))
        {
            Console.WriteLine("检测到未设置更新页面地址，请输入更新页面地址并回车进行设置。");
            var tryUrl = Console.ReadLine();

            // check if the url is valid
            while (!string.IsNullOrEmpty(tryUrl) || !Uri.TryCreate(tryUrl, UriKind.Absolute, out _))
            {
                Console.WriteLine("输入的地址不是有效的 URL，请重新输入更新页面地址并回车进行设置。");
                tryUrl = Console.ReadLine();
            }

            Console.WriteLine("设定更新页面地址成功。");
            shouldUpdateConf = true;
            config.UpdateUrl = tryUrl;
        }

        if (config.UpdateVersion is null)
        {
            config.UpdateVersion = -1;
            shouldUpdateConf = true;
        }

        if (shouldUpdateConf)
        {
            UpdateConfig(config);
            Console.WriteLine("更新 conf.json 成功。");
        }

        return config;
    }

    static void UpdateConfig(Config config)
    {
        try
        {
            var jsonString = File.ReadAllText("./config.json");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var updatedJsonString = JsonSerializer.Serialize(config, options);
            File.WriteAllText("./config.json", updatedJsonString);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine("更新 conf.json 失败，请检查文件权限。");
            throw;
        }
    }

    static Config GetConfig()
    {
        try
        {
            var file = File.OpenRead("./config.json");
            var json = JsonSerializer.Deserialize<Config>(file);
            file.Close();
            return json!;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine("读取设定失败，尝试重建 config.json");

            try
            {
                // remove old config.json if exists
                if (File.Exists("./config.json"))
                {
                    File.Delete("./config.json");
                }

                var newConfig = new Config()
                {
                    GameRootPath = string.Empty,
                    UpdateUrl = DefaultRemoteUpdateUrl,
                    UpdateVersion = -1,
                };
                var serializeOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
                var defaultSetting = JsonSerializer.Serialize(newConfig, serializeOptions);
                File.WriteAllText("./config.json", defaultSetting);
                return newConfig;
            }
            catch (Exception e2)
            {
                Console.WriteLine(e2.Message);
                Console.WriteLine("重建 config.json 失败，退出程序，请检查文件权限。");
                throw;
            }
        }
    }
}