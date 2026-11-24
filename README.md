# Bing Wallpaper for Windows

A system-tray app for Windows that automatically downloads the newest
[Bing daily wallpapers](https://www.bing.com/HPImageArchive.aspx), archives
them under `Pictures\bing-wallpapers`, and sets the newest one as the
desktop wallpaper on every monitor. No more Microsoft Spotlight surprises —
you choose the picture source, the folder, and the file names.

适用于 Windows 的系统托盘应用：自动下载 Bing 每日壁纸，归档到
`图片\bing-wallpapers`，并把最新一张设为所有显示器的桌面壁纸。
告别微软 Spotlight 的不可控——图片来源、保存目录、文件名都由你说了算。

---

## About this app / 关于这个应用

This is the Windows sibling of
[BingWallpaper-for-Mac](https://github.com/iam-otakun/BingWallpaper-for-Mac),
built to the same architecture by **Otakun** together with **Dash**, his
Muse assistant: a tray-resident app that fetches the 8 most recent Bing
archive entries, downloads only the images not yet on disk (UHD), resolves
file names from market metadata, prunes old images, and gives explicit
feedback when you refresh by hand.

这是 [BingWallpaper-for-Mac](https://github.com/iam-otakun/BingWallpaper-for-Mac)
的 Windows 姊妹版，由 **Otakun** 与他的 Muse 助手 **Dash** 按同一套架构
实现：常驻托盘，拉取 Bing 最近 8 条归档，只下载本地还没有的图（UHD），
按市场元数据生成文件名，自动清理旧图，手动刷新时明确弹窗反馈结果。

### Features / 功能

- **Daily automatic update / 每日自动更新** — Fetches on launch and every
  30 minutes whenever today's image is still missing, then sets it as the
  wallpaper. 启动时与每 30 分钟检查一次，当天图还没到就自动补上并设为壁纸。
- **Refresh with feedback / 刷新有回音** — "Refresh Images" reports its
  result: "Update successfully!" when new wallpapers arrived, "Up to date!"
  when there was nothing new, "Update failed!" if the fetch failed.
  点「Refresh Images」会弹窗报告结果：有新图「Update successfully!」，
  没有更新「Up to date!」，失败「Update failed!」。
- **Open Photo Folder / 打开照片目录** — A menu entry opens the folder
  where the wallpapers are saved. 菜单一项直达壁纸保存目录。
- **Previous / Next Day / 前一天、后一天** — Step through the downloaded
  archive from the tray menu, and jump straight back with **Today**.
  在托盘菜单里翻看已下载的归档并切换壁纸，再用「Today」一键回到今天。
- **Stick With This Picture / 钉住这张** — Fallen for one picture? Check
  this menu item and the wallpaper stays put: new photos keep downloading
  into the archive every day, they just are not set. Uncheck it and
  today's picture goes up immediately. The pinned picture is also exempt
  from the retention cleanup. 特别喜欢某张就打钩钉住：每天照常拉新图
  入库，只是不再更换壁纸；取消打钩，立刻换上今天的图。钉住的图也不会
  被保留期清理删掉。
- **Custom file names / 自定义文件名** — Three formats, same as the Mac
  version: English, bilingual (Chinese + English), or local language.
  三档文件名格式，与 Mac 版一致：英文、双语（中文+英文）、本地语言。
- **Retention / 保留期** — Keep images 5 / 10 / 50 / 100 days, or forever.
  保留 5 / 10 / 50 / 100 天，或永久保留。
- **Launch at login / 开机自启** — Optional, via the current user's Run
  key. 可选开机自启（当前用户 Run 注册表项）。

### A note on bilingual names / 关于双语文件名的一点实测

Bing steers markets by exit IP, not only by the `mkt` parameter: from a
China exit, a request for `mkt=en-US` is answered with zh-CN metadata
anyway (verified 2026-11-24: identical `OHR.` id and Chinese copyright).
The app detects this — if the "English" keywords come back identical to
the Chinese ones, it names the file with the Chinese keywords alone
instead of duplicating them after a hyphen. On an exit where en-US
metadata is really served, full bilingual names appear as designed.

Bing 按出口 IP 分市场，不只看 `mkt` 参数：国内出口请求 `mkt=en-US`，
拿回来的仍是 zh-CN 元数据（2026-11-24 实测：`OHR.` 标识与中文版权文本
完全一致）。App 会识别这种情况——若「英文」关键词与中文相同，就只用
中文关键词命名，不再在连字符后重复一遍；在真正能拿到 en-US 元数据的
出口下，双语文件名会按设计完整出现。

## Build / 构建

Single-file C# (C# 5, .NET Framework 4.8). No SDK, no NuGet — `csc.exe`
ships with Windows:

单文件 C#（C# 5，.NET Framework 4.8）。无需 SDK、无需 NuGet——`csc.exe`
随 Windows 自带：

```
build.cmd
```

produces `BingWallpaper.exe`. Headless verification:
`BingWallpaper.exe --selftest` runs one update cycle and writes the
result to `%AppData%\BingWallpaper\selftest.txt`.

生成 `BingWallpaper.exe`。无界面验证：`BingWallpaper.exe --selftest`
跑一个完整更新周期，结果写入 `%AppData%\BingWallpaper\selftest.txt`。

## Credits / 作者

By **Otakun** ([@iam-otakun](https://github.com/iam-otakun)) and **Dash**,
his Muse assistant — idea by Otakun, code by Dash, decisions made together.

由 **Otakun**（[@iam-otakun](https://github.com/iam-otakun)）与他的 Muse
助手 **Dash** 完成——idea 出自 Otakun，代码出自 Dash，决定一起拍板。
