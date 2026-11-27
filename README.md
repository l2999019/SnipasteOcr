# SnipasteOCR

仿 Snipaste 的截图 OCR 小工具:按热键框选屏幕区域,本地离线识别文字,支持部分选词、复制、保存。

基于 .NET 10 WinForms + NativeAOT,发布后是**单个原生 exe**,无需安装 .NET 运行时;OCR 引擎 [SimdPaddleOCR](https://github.com/Sdcb/SimdPaddleOCR) 与中文识别模型全部内嵌,识别过程**完全离线**。

## 功能

- **F1** — 截屏 OCR:框选区域后弹出结果窗口
- **F2** — 截屏复制:框选区域直接以 PNG 复制到剪贴板
- 支持鼠标右键托盘菜单触发,热键被占用时会提示
- **快捷键可自定义** — 托盘菜单 `快捷键设置...` 里点中输入框直接按下新组合键即可;仅 `F1`-`F24` 可单独使用,其余按键需搭配 `Ctrl` / `Alt` / `Shift` / `Win`
- 识别结果窗口:
  - 按截图原始尺寸 1:1 显示,缩放时窗口跟随图片大小联动
  - 单击选中整个文本块,在文本块内**按住拖动可部分选词**(像选网页文字一样)
  - `Ctrl+C` 复制选中内容(整行或选中的字符范围),`Ctrl+A` 全选
  - `Ctrl+滚轮` / `+` / `-` 缩放,`0` 适应窗口,`Esc` 关闭
  - 一键复制全部 / 保存截图为 PNG
 
## 效果图
- **标注工具栏** —
<img width="712" height="593" alt="10c1da7a-454c-472d-99cb-38a3011a14fe" src="https://github.com/user-attachments/assets/ba8f9fa7-539e-4882-a8f1-8c12cc75ce08" />

- **OCR识别效果** —
<img width="546" height="221" alt="image" src="https://github.com/user-attachments/assets/2f90f98c-e077-4c05-8813-6ef5b44c428e" />

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11 (x64) |
| CPU | 需支持 AVX2(2011 年后的 Intel/AMD 桌面/移动 CPU 基本都支持) |

> AVX2 是 SimdPaddleOCR 在 NativeAOT 下启用 SIMD 内核的硬性要求,否则推理速度会差一个数量级,因此发布时固定编译为 AVX2 指令集。

## 快速开始(发布产物)

发布后的 `SnipasteOcr.exe` 单文件即可运行(根据选择模型不同,约37MB-156MB):

```
SnipasteOcr.exe      # 启动后驻留系统托盘
```

启动后按 **F1** 框选区域,稍等识别完成即可在结果窗口中选择、复制文字。

## 自定义快捷键

快捷键保存在:

```
%APPDATA%\SnipasteOcr\hotkeys.txt
```

纯文本,可以直接手工编辑(改完重启程序生效):

```
snip_ocr=Ctrl+Alt+1
snip_image=F2
```

- 托盘菜单 `快捷键设置...` 里录制新组合键,`保存` 后立即生效,无需重启;`恢复默认` 一键回到 `F1` / `F2`
- 新组合键被其他程序占用时**自动回滚**成原快捷键并给出提示,不会出现"热键没了"的状态
- 注册带 `MOD_NOREPEAT`,按住不放不会连续触发截图
- 首次运行没有配置文件时按默认 `F1` / `F2`;若默认值也被占用,会弹窗说明

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
# 调试运行
dotnet build SnipasteOcr -c Debug

# AOT 发布 (win-x64 单文件原生 exe)
dotnet publish SnipasteOcr -c Release -r win-x64 --self-contained -p:PublishAot=true
```

产物位于 `SnipasteOcr/bin/Release/net10.0-windows/win-x64/publish/`,其中的语言资源目录(`cs`、`de`、`zh-Hans` 等)可删除,不影响运行。

## 项目结构

```
SnipasteOcr/
├── Program.cs            # 入口: 手动消息循环 + 全局热键注册 (F1/F2)
├── SnipCoordinator.cs    # 截图流程调度
├── SnipOverlayForm.cs    # 全屏截图覆盖层: 框选、遮罩、尺寸提示
├── OcrService.cs         # OCR 引擎封装 (懒加载、独立线程推理)
├── OcrResultForm.cs      # 结果窗口: 框叠加、字符级拖选、缩放联动
├── HotkeySettings.cs     # 热键模型: 虚拟键码 <-> "Ctrl+Alt+1" 文本、配置文件读写
├── HotkeyManager.cs      # 热键注册/热更新: 失败回滚,配置持久化
├── HotkeyDialog.cs       # 快捷键设置对话框 (按键录制、校验、冲突提示)
├── TrayController.cs     # Win32 托盘图标
└── NativeMethods.cs      # Win32 P/Invoke
```

## 技术要点

- **NativeAOT + 裁剪**: `IsAotCompatible=true`、`IlcInstructionSet=avx2`;OCR 引擎与模型通过程序集嵌入资源加载,需在 `TrimmerRootAssembly` 中显式保留,`PublishAot` 默认开启。
- **Per-Monitor DPI v2**: 截图是物理像素,窗口客户区是逻辑像素,绘制/命中/字符定位三套坐标统一经 DPI 换算,保证 100% 缩放下像素对齐。
- **字符级选词**: PaddleOCR 只输出整行文本与整行检测框(四边形)。选词按字符宽度权重(汉字 1.0 / ASCII 0.6 / 空格 0.35)将字符投影到检测框主轴上,鼠标位置经投影反解出行内字符索引;复制内容直接按字符索引切原文,结果精确。
- **推理不锁图**: 识别前对位图做瞬时 `LockBits` 拷贝,推理在独立字节缓冲上进行,UI 线程可随时重绘。
- **热键热更新**: 改键时先在宿主窗口 `UnregisterHotKey`,再注册新组合;任一失败即回滚旧配置。菜单文字与托盘提示随之重建,`HotkeySettings` 是菜单文案与提示文案的唯一来源。
- **按键录制**: 录入框屏蔽全部输入,主键取 `e.KeyCode`,修饰键用 `GetKeyState` 现场查询 (WinForms 的 `e.Control/e.Alt` 认不出 `Win` 键,`Alt` 组合下也不可靠)。

## 已知限制

- 中文识别为主(中文 V6 Medium 模型),英文等西文字符可用但非最优
- 字符级选区按宽度比例估算,选区边缘与真实字符边界可能有 1~2 像素偏差(不影响复制内容)
- 仅支持 x64 Windows;发布产物与编译机架构绑定,换架构需重新 publish
- `PrintScreen` 无法作为全局热键注册;录制 `Win` 组合键时系统可能同时弹出开始菜单(不影响录制结果)

## 依赖

- [Sdcb.SimdPaddleOCR](https://github.com/Sdcb/SimdPaddleOCR) 1.4.2 — 纯 C# 的 PaddleOCR 推理引擎
- [Sdcb.SimdPaddleOCR.Models.ChineseV6Medium](https://www.nuget.org/packages/Sdcb.SimdPaddleOCR.Models.ChineseV6Medium) 1.0.0 — 中文 det+rec+cls 模型与字典(嵌入资源)

## License

MIT,(依赖项目开源协议保持原协议)
