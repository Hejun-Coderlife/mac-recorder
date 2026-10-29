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
    ExportOptions opt = null;
    string E(string k) => Environment.GetEnvironmentVariable(k);
    if (E("STUDIO_RES") != null || E("STUDIO_FPS") != null || E("STUDIO_QUALITY") != null || E("STUDIO_FORMAT") != null)
        opt = new ExportOptions { Resolution = E("STUDIO_RES") ?? "1440", Fps = int.TryParse(E("STUDIO_FPS"), out var f) ? f : 60, Quality = E("STUDIO_QUALITY") ?? "high", Gif = E("STUDIO_FORMAT") == "gif" };
    var cts = new System.Threading.CancellationTokenSource();
    if (double.TryParse(E("STUDIO_CANCEL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cs)) cts.CancelAfter(TimeSpan.FromSeconds(cs));
    try { Studio.Render(args[1], args[2], new StudioStyle(), p => { }, null, Console.WriteLine, null, opt, cts.Token); }
    catch (OperationCanceledException) { Console.WriteLine("CANCELLED exists=" + File.Exists(args[2])); return 3; }
    return 0;
}
if (args.Length > 0 && args[0] == "audio")
{
    var info = Studio.Probe(Path.Combine(args[1], "raw.mp4")); var tr = Studio.LoadTrack(args[1], info);
    var pj = StudioProject.Load(args[1], tr, info.Duration);
    Console.WriteLine("RenderAudio " + Studio.RenderAudio(args[1], info, tr, pj, args[2]));
    return 0;
}
if (args.Length > 0 && args[0] == "test")
{
    string dir = args.Length > 1 ? args[1] : null, mac = args.Length > 2 ? File.ReadAllText(args[2]) : null;
    bool ok = true;
    foreach (var res in new[] { StudioTests.Plan(dir), StudioTests.Project(mac), StudioTests.Scrub(), StudioTests.Presets(), StudioTests.Looks(Studio.LoadCover), StudioTests.Cuts() })
    { Console.Write(res); ok &= res.TrimEnd().EndsWith(" OK"); }
    if (dir != null) { var b = StudioTests.Blur(dir); Console.Write(b); ok &= b.TrimEnd().EndsWith(" OK"); }
    return ok ? 0 : 1;
}
if (args.Length > 0 && args[0] == "whisper")
{
    var res = StudioTestsW2.Whisper(args[1], wav => Whisper.Transcribe(wav, f => { }, System.Threading.CancellationToken.None));
    var mk = StudioTestsW2.Maker("/tmp/w2maker", args[1], (wav, prog) => Whisper.Transcribe(wav, prog, System.Threading.CancellationToken.None));
    Console.Write(res + mk); return res.TrimEnd().EndsWith(" OK") && mk.TrimEnd().EndsWith(" OK") ? 0 : 1;
}
if (args.Length > 0 && args[0] == "w2")
{
    bool ok = true;
    foreach (var res in new[] { StudioTestsW2.Voice(), StudioTestsW2.Captions(), StudioTestsW2.Export(args.Length > 1 ? args[1] : "/tmp/w2work", args.Length > 2 ? args[2] : null) })
    { Console.Write(res); ok &= res.TrimEnd().EndsWith(" OK"); }
    return ok ? 0 : 1;
}
if (args.Length > 0 && args[0] == "w3")
{
    bool ok = true;
    foreach (var res in new[] { StudioTestsW3.Rules(), StudioTestsW3.Export(args.Length > 1 ? args[1] : "/tmp/w3work") })
    { Console.Write(res); ok &= res.TrimEnd().EndsWith(" OK"); }
    return ok ? 0 : 1;
}
if (args.Length > 0 && args[0] == "w4")
{
    bool ok = true;
    var list = new System.Collections.Generic.List<string> { StudioTestsW4.Rules() };
    if (!(args.Length > 1 && args[1] == "rules")) list.Add(StudioTestsW4.Export(args.Length > 1 ? args[1] : "/tmp/w4work"));
    foreach (var res in list) { Console.Write(res); ok &= res.TrimEnd().EndsWith(" OK"); }
    return ok ? 0 : 1;
}
Console.WriteLine("usage: render <dir> <out.mp4> | test [dir] [mac project.json]");
return 2;
