# Poso

**Poso** 是一个使用纯托管 C# 编写的 .NET PDF 查看与渲染库。
它通过一层精简的、与后端无关的图形抽象来解析和渲染 PDF 文档，同一套核心既可以用 SkiaSharp 绘制，
也可以用纯托管的 PeachDrawing 光栅化器绘制，或者接入任何你自己实现的后端——
并且自带一个可以直接嵌入应用的 [Avalonia](https://avaloniaui.net/) 查看器控件。

[English](README.md)

## 功能特性

**解析**

- 交叉引用表、交叉引用流、对象流，以及损坏文件的自动恢复
- 标准安全处理器：加密文档、用户/所有者密码、异步密码回调
- 整个文件驻留内存，随机访问快速、线程安全简单（面向查看器场景）

**内容解码**

- 过滤器：`FlateDecode`（含 PNG/TIFF 预测器）、`LZWDecode`、`ASCII85Decode`、
  `ASCIIHexDecode`、`RunLengthDecode`
- 托管代码实现的图像编解码器：JPEG、JBIG2、CCITT 传真；JPEG 2000（`JPXDecode`）
  由托管的 CoreJ2K 解码器完成
- 字体：Type 1、CFF/OpenType、Type 0（CID）、Type 3、内嵌字体与 14 种标准字体，
  CMap 与 ToUnicode 映射

**渲染**

- 路径、裁剪、文本（全部绘制模式）、图像、渐变着色（类型 1–7）、图案、
  透明度组、软蒙版、混合模式、表单 XObject
- 注释外观流
- 可选内容（图层），支持按组控制可见性

**文档功能**

- 页面树，支持旋转、裁剪框与用户单位
- 文本提取（行/片段及命中矩形），以及页内文本搜索
- 链接、跳转目标、大纲（书签）、AcroForm 表单域

**查看器**

- `PdfView` Avalonia 控件：单页与连续布局、适配页面 / 适配宽度 / 自定义缩放、
  旋转、搜索与命中跳转、链接激活与页码变更事件
- 运行时在 Skia 与 PeachDrawing 后端之间切换

## 项目组成

| 项目                   | 说明                                                     |
| ---------------------- | -------------------------------------------------------- |
| `src/Poso`             | 核心：PDF 解析、文本，以及与后端无关的渲染管线           |
| `src/Poso.Skia`        | 基于 SkiaSharp 的图形后端                                |
| `src/Poso.PeachDrawing`| 基于 PeachDrawing 的图形后端（纯托管，无本地依赖）       |
| `src/Poso.Avalonia`    | `PdfView` 控件、页面渲染管线、位图转换                   |
| `tests/Poso.Tests`     | xUnit 测试集                                             |
| `samples/Sample.Avalonia` | 完整查看器示例：Desktop、Browser、Android、iOS         |

## 快速开始

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

### 将页面渲染为位图

```csharp
using Poso.Pdf;
using Poso.Skia;

using PdfDocument document = PdfDocument.Open("example.pdf");
PdfPage page = document.Pages[0];

const double scale = 2.0; // 72 dpi 的 PDF 用户空间的 2 倍
int width  = (int)Math.Ceiling(page.Width  * scale);
int height = (int)Math.Ceiling(page.Height * scale);

IGraphicsFactory factory = SkiaGraphicsFactory.Default;
using IGraphicsContext context = factory.CreateContext(width, height);
page.Render(factory, context, new PdfRenderOptions((float)scale, 0f, PosoColor.White));
using IImage image = context.Snapshot();
// image.CopyPixels(...) 可获取 BGRA 像素数据，也可按后端各自转换
```

把 `SkiaGraphicsFactory.Default` 换成 `Poso.PeachDrawing.PeachGraphicsFactory.Default`，
即可在没有任何本地（原生）依赖的情况下渲染——你的其余代码一行都不用改。

### 打开带密码的文档

```csharp
PdfDocument document = await PdfDocument.OpenAsync(path, () => PromptPassword());
if (document.NeedsPassword)
{
    // 回调返回的密码不正确，需要重新询问
}
```

### 提取文本

```csharp
PdfTextPage text = document.Pages[0].GetText(SkiaGraphicsFactory.Default);
string wholePage = text.Text;
IReadOnlyList<TextLine> lines = text.Lines;
IReadOnlyList<TextHit> hits = text.Search("Poso");
```

### 嵌入查看器（Avalonia）

```xml
xmlns:controls="using:Poso.Avalonia.Controls"
...
<controls:PdfView Source="{Binding Document}"
                  ZoomMode="FitWidth"
                  PageLayout="Continuous"
                  PageChanged="OnPageChanged"
                  LinkActivated="OnLinkActivated" />
```

完整功能（大纲、缩略图、搜索、密码提示、后端实时切换）参见 `samples/Sample.Avalonia`。

## 构建与测试

```bash
dotnet build Poso.slnx
dotnet test tests/Poso.Tests
dotnet run --project samples/Sample.Avalonia/Sample.Avalonia.Desktop
```

## 渲染保真度

Poso 的渲染结果以 **PDFium**（Chrome 的引擎）为基准，在 pdf.js 语料库（`pdfjs/test/pdfs`，
977 个 PDF，第 0 页，scale 2.0）上验证。工具在 [`tools/corpus`](tools/corpus)，结论记录在
[`tools/corpus/findings_pdfium_zh_CN.md`](tools/corpus/findings_pdfium_zh_CN.md)。

| 与 PDFium 对比（953 个可比文件） | Skia | Peach |
| --- | --- | --- |
| 像素差 1% 以内 | 552 | 559 |
| 像素差 5% 以内 | 822（86.3%） | 824（86.5%） |
| Poso 打不开的文件 | 10 | 10 |

排除 PDFium 自身不可靠的文件（它忽略 `/UserUnit`，也不画注释外观流）后，没有任何可比文件与
基准的差异超过 50%；「Poso 全白、PDFium 有内容」只剩一页注释 chrome。整条流水线每个后端约
四分钟渲染完全部语料，并逐像素对比每一页。

## 许可证

MIT 协议，详见 [LICENSE](LICENSE)。
