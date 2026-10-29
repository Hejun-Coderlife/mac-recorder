// 用法：dotnet run -- 素材文件夹 输出.mp4 [ffmpeg 路径]
using System;
using ScreenRecorder;

Studio.FfmpegPath = args.Length > 2 ? args[2] : "/opt/homebrew/bin/ffmpeg";
Studio.Render(args[0], args[1], new StudioStyle(), p => { }, null, Console.WriteLine);
