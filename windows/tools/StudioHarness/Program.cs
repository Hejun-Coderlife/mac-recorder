// 在 Mac / Linux 上用 .NET 8 跑 Studio 的合成和自测（StudioCore.cs / StudioModel.cs / StudioTests.cs），不进安装包
// 用法：
//   dotnet run -- render 素材文件夹 输出.mp4 [ffmpeg]      按素材夹里的 project.json 导出
//   dotnet run -- test [素材文件夹] [mac 的 project.json]   跑 PLANTEST / PROJTEST / SCRUBTEST / PRESETTEST / LOOKTEST（有素材夹再跑 BLURTEST）
using System;
using System.IO;
using ScreenRecorder;

Studio.FfmpegPath = Environment.GetEnvironmentVariable("FFMPEG") ?? "/opt/homebrew/bin/ffmpeg";
if (args.Length > 0 && args[0] == "render")
{
    Studio.Render(args[1], args[2], new StudioStyle(), p => { }, null, Console.WriteLine);
    return 0;
}
if (args.Length > 0 && args[0] == "test")
{
    string dir = args.Length > 1 ? args[1] : null, mac = args.Length > 2 ? File.ReadAllText(args[2]) : null;
    bool ok = true;
    foreach (var res in new[] { StudioTests.Plan(dir), StudioTests.Project(mac), StudioTests.Scrub(), StudioTests.Presets(), StudioTests.Looks(Studio.LoadCover) })
    { Console.Write(res); ok &= res.TrimEnd().EndsWith(" OK"); }
    if (dir != null) { var b = StudioTests.Blur(dir); Console.Write(b); ok &= b.TrimEnd().EndsWith(" OK"); }
    return ok ? 0 : 1;
}
Console.WriteLine("usage: render <dir> <out.mp4> | test [dir] [mac project.json]");
return 2;
