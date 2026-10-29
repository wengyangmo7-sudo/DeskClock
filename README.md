# DeskClock · 桌面时钟

一个 Windows 桌面时间挂件。无边框、逐像素透明，可以「钉」在桌面底层——平时一直在，切到浏览器、游戏、终端时被窗口自然遮挡，不会浮在最前面挡事。

![右键菜单](预览-菜单.png)

## 特性

- **逐像素透明**：`UpdateLayeredWindow` + 32bpp 预乘 alpha。不是「抠透明色」，文字边缘没有黑边，圆角和羽化都平滑。
- **6 种背景**：深色玻璃 / 羽化玻璃 / 取色自适应 / 磨砂玻璃 / 纯文字·阴影 / 纯文字·描边
- **4 档不透明度**：80% / 100% / 125% / 150%
- **5 档字号**：版面按「最宽字形」测量，秒数跳变时窗口尺寸不抖，秒数右对齐
- **3 种显示层级**：桌面底层 / 普通窗口 / 置顶显示
- **DPI 感知**：150% 缩放下文字依然锐利
- 单实例运行、不占任务栏、不进 Alt+Tab
- 右键菜单用对号标出当前状态，设置自动保存
- 整个程序是单文件 C#，只依赖 .NET Framework 自带的 `System.Drawing` / `System.Windows.Forms`，无任何第三方库

## 运行

双击 `DeskClock.exe`，需要 .NET Framework 4.x（Windows 10 / 11 自带）。

首次运行出现在屏幕右上角，按住左键拖动即可移动。

## 使用

| 操作 | 说明 |
| --- | --- |
| 左键拖动 | 移动位置（勾选「锁定位置」后不可拖动） |
| 右键 | 打开菜单 |
| 鼠标滚轮 | 快速切换字号 |

菜单项：显示秒 / 显示日期 / 24 小时制 / 背景 / 不透明度 / 字号 / 锁定位置 / 显示层级 / 开机自启 / 回到右上角 / 退出

## 显示层级

| 选项 | 行为 |
| --- | --- |
| 桌面底层（默认） | 一直显示，但永远压在其它窗口下面。切到浏览器、游戏时被遮住，回到桌面又露出来；也不会被 Wallpaper Engine 的动态壁纸盖住 |
| 普通窗口 | 和别的窗口平级，点了会浮到前面 |
| 置顶显示 | 永远在最前 |

「桌面底层」的实现方式，是把窗口插到 **最靠前的 `Progman` / `WorkerW` 窗口之上、所有应用窗口之下** 的那一格。直接用 `HWND_BOTTOM` 会被壁纸层盖住，所以没有用裸沉底。

## 编译

```bat
build.bat
```

或者手动：

```bat
csc /nologo /codepage:65001 /target:winexe /out:DeskClock.exe ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll DeskClock.cs
```

## 文件

| 文件 | 说明 |
| --- | --- |
| `DeskClock.cs` | 全部源码（单文件，约 56 KB） |
| `DeskClock.exe` | 已编译好的程序 |
| `使用说明.txt` | 更详细的中文说明 |
| `预览-*.png` | 背景样式与菜单的实际渲染效果 |

## 已知限制

- **3 号「取色自适应」与 4 号「磨砂玻璃」需要抓屏**：组件会先把自身隐藏，再截取卡片所在区域并模糊。若抓屏被系统或安全软件拦截，会自动回退成 1 号深色玻璃，不会报错崩溃。
- 壁纸是动态的（Wallpaper Engine）时，4 号的模糊是抓屏那一刻的**定格**；壁纸动画仍在跑，但卡片里的模糊不会跟着动。这种场景建议用 2 号羽化玻璃。
- 抓屏是否可用可以自检：运行 `DeskClock.exe --selftest`，看 `selftest.txt` 里的「抓屏可用」一行。

## 自检 / 排查

| 命令 | 产物 |
| --- | --- |
| `DeskClock.exe --selftest` | `selftest.txt`：DPI、表面尺寸、字体、分层窗口推送、背景、抓屏是否可用、前台窗口类 |
| `DeskClock.exe --vistest` | `vistest.txt`：隐藏 / 重新显示的实际效果 |
| `DeskClock.exe --ztest` | `ztest.txt`：逐层列出窗口 z 序，验证是否真的在桌面层之上、应用窗口之下 |
| `DeskClock.exe --bg 3` | 用指定背景样式启动（0–5），方便对比 |
| `DeskClock.exe --preview --wall <图片> --out <目录>` | 生成六种背景的对比预览图 |
| `DeskClock.exe --menushot` | 生成菜单实际渲染图 |

## 设置文件

`%APPDATA%\DeskClock\settings.ini`，退出时自动保存。

键：`seconds` `date` `hour24` `bg` `bgopacity` `size` `zorder` `locked` `x` `y`

## 技术备注

- 窗口用 `WS_EX_LAYERED | WS_EX_TOOLWINDOW`，配合 `UpdateLayeredWindow` 推送 32bpp DIB，实现真正的逐像素透明。
- 桌面底层模式监听 `WM_WINDOWPOSCHANGED`，一旦 z 序被改动就重新摆位，另有约 1 秒一次的兜底校正。
- GDI+ 写入 DIB 时像素已是预乘 alpha，所以 `BLENDFUNCTION` 用 `alphaFmt = 1`、`flags = 0`。