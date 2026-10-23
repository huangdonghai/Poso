# Poso 与 PDFium 对比 —— 语料库扫描结果

语料库：`D:\_git0\pdfjs\test\pdfs`（977 个 PDF），第 0 页，scale 2.0（144 dpi）。
参照引擎：PDFium（通过 `pypdfium2` 渲染，输出 `out/*-pd.png`）。不再使用 MuPDF。
本报告由 `cmp_pdfium.py` 生成；原始数据见 `out/report_pdfium.tsv`，工作清单见 `out/triage_pdfium.txt`。

## 覆盖率

| | 成功 | 失败 |
| --- | --- | --- |
| PDFium | 954 | 23（13 个加密、9 个格式损坏、1 个内存溢出） |
| Poso/Skia | 967 | 10 |
| Poso/Peach | 967 | 10 |

Skia 与 PDFium 可比对的文件有 953 个，Peach 与 PDFium 可比对的有 953 个。有 9 个文件两个引擎都渲染不出页面。

## 一致程度（像素差 >16/255 的占比）

| 档位 | Skia | Peach | Skia，排除参照引擎盲区后 |
| --- | --- | --- | --- |
| 几乎一致（<1%） | 550 | 555 | 548 |
| 接近（<5%） | 266 | 263 | 258 |
| 中等（<20%） | 126 | 124 | 115 |
| 较大（<50%） | 8 | 8 | 4 |
| 巨大（≥50%） | 3 | 3 | 0 |

也就是说，可比较文件中 85.6% 与 PDFium 的差异在 5% 以内，98.8% 在 20% 以内；排除 PDFium 自身盲区后没有 ≥50% 的文件。另外「Poso 全白、PDFium 有内容」的文件从 11 个降到各后端 **1 个**（`rc_annotation`，注释 chrome，按计划只记录不修）。

## 有 24 个文件 PDFium 本身不是可靠的参照

这些文件不计入「Poso 出错」的统计，但会记录在 `out/report_pdfium.tsv` 的 `caveat` 列中：

- `oracle-annots`（21 个）—— `FPDF_RenderPageBitmap` 不绘制标注（annotation）的外观流，而 Poso 会绘制（这是 Poso 的既定设计，见 README）。因此页面画布会差出整个标注。例如：`annotation-tx2`、`bug1844576`、`160F-2019`（AcroForm 字段底色）、`resetform`。
- `oracle-userunit`（3 个）—— PDFium 忽略 `/UserUnit`，这几处**正确的一方是 Poso**：`issue19176`（`/UserUnit 72`）、`bug1947248_text` / `bug1947248_forms`（`/UserUnit 3`）。
- `PDFIUM-BLANK`（1 个）—— `jbig2_file_header`：PDFium 无法解码带 JBIG2 文件头的 JBIG2 流，整页画成空白；Poso 能正常解码（43% 墨迹）。pdf.js 把这个文件标为 `type: eq`，说明应当有内容，因此可疑的一方是 PDFium。

## 第二轮 —— 已修复的问题，以及关于 Peach 数字的警告

第一轮之后落地了两个修复（300 个单元测试全部通过，新增 3 个）：

1. **box 数组里带间接引用的元素现在会逐个解析**（`PdfDocument.GetArrayNumber`，用于页面 box、Form 的
   `/BBox` 与 `/Matrix`、平铺图案的 `/BBox`、以及标注的 `/Rect`/`/BBox`）。全语料验证结果：
   `franz_2` 的 Peach 从 97.31% 降到 2.83%；`issue7872` 现在渲染成 500x100 而不是 1224x1584
   （sk 6.19 → 4.97，pe 6.19 → 4.35）；`issue15441` 7.41 → 2.05；`issue10542_reduced` 6.16 → 2.16；
   `issue11403_reduced` 5.98 → 4.01；`issue8480` 和 `poppler-90-0-fuzzed`（此前挂死超过 60 秒）
   现在都能渲染。没有任何文件变差。
2. **CalGray/CalRGB 现在会把声明的白点适应到 D65**（`Cielab.AdaptToD65`）。`calgray` 每个色块原本都带
   粉色偏色，现在已是中性灰。注意这个文件上**PDFium 才是错的一方**：PDFium 直接输出 `g*255`、跳过了
   传输函数，而语料期望的是 pdf.js 自己的参考公式（`295.8*L^(1/3)-40.8`）。Poso 与该公式的平均差从
   8.69 降到 4.83，PDFium 则是 45.04。`pct>16` 那一列没有变化，因为两个引擎分歧在色调映射而非色相。

**本报告里的 Peach 数字依赖上下文。** 同一个页面在 Peach 下的输出会随「同进程之前渲染过什么」而变：
`ZapfDingbats` 至少有三种不同结果，用 `bug900822.pdf` 加 `ZapfDingbats.pdf` 两个文件即可复现。
同一份二进制的两次全量运行逐字节相同，而 Skia 在任何上下文下都逐字节相同——所以在单线程的扫描里
这是确定性的「每进程缓存」效应，且只影响 Peach。该路径上唯一的进程级可变状态是 `PeachFont.SharedFontSet`
（typeface 存活整个进程）；两种上下文里的字体**选择**完全相同，因此分歧出在 PeachDrawing.Text 内部的
缓存。同一效应也让单元测试出现顺序依赖，而且在 xUnit 的并行运行下连同一次运行都不可复现：同一份二进制
连续跑三次，失败数分别是 0、0、1，失败的具体测试也不同（先是
`OddLengthDashArray_StillStrokesTheLine`，然后是
`SoftMaskTests.ExtGStateMaskOnATransparencyGroup_SurvivesTheFormClearingIt`）。把这两个修复 stash
掉之后它们同样会失败。因此，Peach 上几个百分点的差异在单文件运行复现之前，都应当当作噪声处理。

## 第三轮 —— 又修了两处，并且又发现一个 PDFium 站错边的文件

1. **`smask_alpha_oob_transfer`（97.22% → 0.00%，两个后端）**：`/S /Alpha` 软掩码的取值是组的 alpha，
   而组的内容被裁到 `/BBox`；框外（以及框内没画到的地方）组是透明的，alpha 为 0，`/TR` 照常作用在
   这个 0 上——所以元素在整片未覆盖区域应以 `TR(0)` 显示。Poso 原来把这些像素强行置 0 并跳过传输函数，
   于是比元素小的掩码会「擦掉」框外区域而不是抬升。PDFium 与 pdf.js 都是抬升（pdf.js 源码原文：
   「Alpha SMasks ignore /BC for the alpha output」，越界 alpha 按 0 走传输表）。其余 17 个软掩码/
   knockout 文件逐字节不变。

2. **`jpx_smaskindata`：`/SMaskInData 2` 的预乘样本要先反预乘**。该条目表示码流里的颜色已经乘过 alpha，
   `/Matte` 则是它们之前合成时用的底色，正确算式是 `C = (C存储 − M)/α + M`（pdf.js
   `core/image.js #undoPreblend` 就是这个）。Poso 把样本当普通颜色又乘了一次 alpha，等于压暗两次，而且
   完全忽略了 `/Matte`。四张 2×1 图像的八个采样点现在都与 pdf.js 一致。

   注意这个文件上**PDFium 是错的一方**：它把预乘样本按不透明直接画出，`/SMaskInData` 为 1 和 2 时都基本
   忽略了掩码。所以修复让「对 PDFium 的差异」从 46.07% 升到 54.38%——这是变正确而不是变差。该文件在
   pdf.js 清单里是 `type: eq`，参考实现就是 pdf.js 自己。

## 第四轮 —— 标注外观里的图案着色

**`issue7821`（Skia 38.70% → 10.73%，Peach 37.22% → 9.21%）**：外观流本身就是一个 Form，所以它的默认
坐标空间就是外观自己的空间（`/BBox` 所在的那个），而图案著色的 `/Matrix` 正是从这个默认空间度量的
（规范 8.7.3.1）。Poso 给外观设了 CTM，却把 `_streamEntryMatrix` 留在页面级，于是外观里的
PatternType 2 由页面变换而非「BBox→Rect」的放置变换定位，坐标轴整体落到了可见矩形之外，整枚印章塌成
渐变的一端。

`issue7821` 是一枚 `/Stamp`：外观用图案填一个圆角矩形，轴长 152pt，而外观被放在
`Rect [448 2.7 611 52]`——用页面级入口矩阵时可见区域整片落在轴的外侧，于是渲染成纯色。修复后三个采样点
在矩形内分别读到近端的红与远端的蓝。其余 34 个标注语料文件逐字节不变（包括所有不带图案的外观），
并且外观返回时入口矩阵会恢复，页面内容里后续的 `sh` 仍然按页面解析。

### 现在的整体数字（已提交 5 个 fix）

- Skia 与 PDFium：520 个几乎一致、246 个 <5%、161 个 <20%、15 个 <50%、3 个 ≥50%
- 排除 27 个已验证「PDFium 站错边」的文件后：**Skia 侧已没有 ≥50% 的文件**（big 11 个）
- 剩下的最大真实差异：`issue16316`（38.16%）、`issue16038`（33.63%）、`issue7229`（28.63%）
- Peach 独有的最大项：`issue12841_reduced`（82.88%，DCTDecode 解码失败导致整页空白，已诊断未修）

## 第五轮 —— 分类剩余的最大差异（尚未修）

把剩下的几项按「放大后是否收敛」分类，避免把光栅化差异当成语义 bug：

- **`issue16316`（38.16%）：字体光栅化差异，不是 bug。** 页面是 60×9.4pt 的一小块公式，用嵌入的 Type 1
  （`NimbusRomNo9L-Regu`）。2 倍时差异 38.16%，**8 倍时降到 9.12%**，字形轮廓、serif、字距逐笔重合——
  小字号下 PDFium 的 FreeType hinting 让笔画更重。这类差异没有可修的语义错误。
- **`issue16038`（33.63% → 8 倍 22.48%）：同类，主要是 hinting**，余下部分待查。
- **`issue14165`（24.96%）：真 bug，而且是缩放相关的，已单独列为下一项。**
- `issue7229`（28.63% → 8 倍 24.27%）、`issue269_2`（27.76% → 27.65%）：放大后不收敛，属于结构性差异，
  待查。

### 下一个具体目标：`issue14165` 的图案着色在缩放后消失

按 1 倍/2 倍渲染时 Poso 还能画出条纹渐变，**按 8 倍渲染时整页几乎空白**（顶部 200 行全是 255，即函数
t=0 处的值），而 PDFium 正常。该页内容先做了一条只有 **0.000061pt 高**的裁剪
（`0 0.000061035 1190.52 841.92 re W* n`），再用图案填一个覆盖整页的矩形。Poso 的
`PdfShading.Rasterize` 用 `context.ClipBounds` 决定栅格尺寸，裁剪退化成一条线时栅格只有 1 像素高，
再被拉伸/丢弃——这解释了 2 倍下的「发虚」和 8 倍下的消失。要看的是：图案填充时该用
**路径包围盒 ∩ 裁剪区**，而不是只用裁剪区。

## 第六轮 —— 又三处修复（`issue14165`、`issue12841_reduced`、`issue7229`）

1. **着色栅格超过 8192 像素时被整体丢弃**（`7cac0d8`）。`PdfShading.Rasterize` 有一条兜底：单张位图
   任一边超过 8192 就返回 null。`issue14165` 页宽 1190.52pt，**6.9 倍起设备宽度就超过 8192，条纹渐变
   整页消失**（5 倍/6 倍正常，6.9/7/8 倍全白，墨迹位置读到 255 即函数 t=0 的值）。改为按 1024 像素分块
   栅格化，每块用同一个「着色→设备」变换，块边界无接缝；8 倍下恢复为 22.45%，2 倍不变（24.96%，
   余下的是 PDFium 自己把渐变欠采样成台阶）。
2. **顺序 JPEG 声明 `Se=0` 被整段拒绝**（`04b1a1d`）。`Ss/Se/Ah/Al` 是渐进扫描的字段，顺序扫描根本
   不读 `Se`，但解码器要求 `Se==63`。`issue12841_reduced` 正是这么写的（pdf.js 的 "reduced" 测试就在
   测这类宽松处理）。Skia 自带解码器不受影响，Peach 没有 JPEG 编解码器、只能走托管解码器，于是整页
   空白：**82.88% → 0.27%**，与 Skia 完全一致。
3. **xref 条目指向了错的对象时应当重建表**（`22d6aab`）。`issue7229` 第一段 xref 的子段头写成
   `1 7`，而文件对象从 0 开始，**整张表错位一号**：`3 0 R` 解出来是 `2 0 obj` 的正文。解析本身成功，
   只是对象不对，页树于是丢掉这个 kid——**文档少了一页**。现在解析后会核对对象号，不一致就按文件扫描
   重建一次表再读。该文件两页都回来了、顺序正确（28.63% → 12.50%），其余语料无变化。

### 整体数字（8 个 fix 已提交）

- Skia 与 PDFium：520 个几乎一致、246 个 <5%、161 个 <20%
- 排除 27 个已验证「PDFium 站错边」的文件后，**Skia 侧没有 ≥50% 的文件**
- 剩下的最大项多为字体 hinting（`issue16316` 放大后从 38% 收敛到 9%）或 PDFium 自身的欠采样

## 第七轮 —— 可选内容（`issue269_2`，27.76% → 4.03%）

`issue269_2` 的页面是 8 套「白格/黑格」变体的棋盘，每套包在 `/OC /MCnn BDC` 里，默认只有
「Black Squares - spiral」(OCG 15) 是开的。Poso 渲染出来是纯 51 灰，根因有两处，都属于**把间接引用当
字面值读**：

1. **`/OFF` 是间接引用**（`/OFF 60 0 R`），`GetArray("OFF")` 不解析引用 → 返回 null → OFF 集合为空 →
   **所有图层都被当成可见**。该文件没有 `/ON`，所以这个错误直接生效。
2. **标记内容的 `/OC` 指向 `/OCMD` 成员字典**，而不是图层本身；成员字典必须按 `/OCGs` 与 `/P` 策略求值。
   Poso 却去问「对象 106 在不在 /OFF 里」——106 是那个成员字典，自然不在——于是每个块都判为可见。

两者叠加，本该关闭的「Cover Black Squares」图层用 `.2 .2 .2` 盖住了本该显示的照片。修好后还顺带发现并
避免了一个陷阱：`/Span <<...>> BDC`、`/Artifact <<...>>` 这类**内联属性字典**既不 OCG 也不 OCMD，必须
视为可见，否则会把它们的整段内容一起隐藏（我第一版改动就踩了这个坑，语料里 73 个文件整页变白，已修正）。

现在标记内容与 Form 两条路径共用同一套求值；`issue18823` 也从 6.15% 降到 4.94%，其余语料无变化。

## 第八轮 —— Peach 的两个「整页空白」，一个修好、一个定位到 PeachText

1. **`issue15716`（Peach 32.75% → 15.46%）——已修（`5458a9e`）**。ZapfDingbats 没有字形程序，字形靠
   Adobe Glyph List 按名字取：`a109` 是 U+2660、`a110` 是 U+2665。所以后端选的替代字体必须覆盖
   Dingbats 区块。**Skia 侧早就为此改过**（注释里就写着 `issue15716`），但 Peach 的映射还指向
   **Wingdings**——它的图形在私有区、对 U+2660 只会返回 glyph 0——于是这个后端上所有 dingbat 都画不出。
   该文件整页就是一个平铺图案、图案格子里两个 dingbat 字形，所以整页空白。
   **根因之所以长期没被发现，是因为对应的单元测试是 `[Fact]` 只跑 Skia 工厂**；已改为覆盖两个后端的 Theory。
2. **`endchar`（Peach 32.55% → 5.34%）——已修（`52f0ee2`）**。根因是 **CFF 的 Type 1 `seac` 复合字形**：
   这类字形的 charstring 里没有轮廓，只有一个带 5 个操作数的 `endchar`（`asb adx ady bchar achar`），
   绘制时要「基字 + 重音字按偏移叠放」。Skia 自己会展开，所以看不出问题；**PeachDrawing 不展开，而且
   静默失败**——`TryGetOutline` 返回 false，字形循环直接跳过，字母凭空消失、字宽却照样占位，于是出现
   "PLAN  TAIRE" 里的空档。该文件是 "PLANÉTAIRE" 裁到只剩一个字母，所以整页差异高达 32.55%、Peach 墨迹
   0.00%（PDFium 32.55%）。

   修法：`CffProgram` 增加 seac 读取器（按 Type 2 语法遍历 charstring，正确跳过 hintmask 的掩码字节，
   取尾部 `endchar` 的 5 个操作数，并把 `bchar`/`achar` 这两个 StandardEncoding 码经 charset 换成 glyph
   id）；`PeachFont` 记住加载的程序，轮廓缺失时把基字画在原点、重音画在 `(adx, ady)`，两者都走普通的
   `AddOutline`，因此文字裁剪与填充两条路径都覆盖到。

   语料里另有 `issue4801`（3.30% → 1.99%）和 `issue7101`（4.14% → 3.02%）同样受益（65 个 CFF 字体中
   有 6 个含 seac，`endchar.pdf` 的 229 个字形里有 56 个是复合字形）。

## 第九轮 —— `bug1308536`：Skia 的 CFF 解析器不接受这份程序（结论：不改）

这个文件上 Skia 22.21%、Peach 7.25%、PDFium 墨迹 10.66%，而 **Skia 墨迹 19.57%**——画得比谁都多。
看图是整行文字糊成黑块，Peach 和 PDFium 却正常。

**表现**：内嵌的是 4.6KB 裸 CFF（`FontFile3 /Type1C`，70 个字形）。SkiaSharp 加载失败 → Poso 按设计回退
到 Arial，并保留 PDF 声明的 `/Widths`（规范要求文本定位按 PDF 宽度，这是对的）。但这份 `/Widths` 是给
**UltraCondensedSansTwo** 这种超窄字体的（123、121、50…），Arial 的字形宽得多，在同样的窄间距里互相
重叠，于是糊成一片。

**定位过程与结论**：

- **逐表二分**（把 stonesans 的 8 张表逐张换进失败容器）：**只有换入 `CFF ` 表能让 Skia 接受**，其余
  7 张表换成别的都无效 → 是 CFF 程序本身，不是 Poso 包装出的 sfnt 容器。
- **这份 CFF 是合法的**：Top DICT 与 Private DICT 都能完整解析且不越界（`/Private [77, 4568]`、
  `defaultWidthX 251`、`nominalWidthX 223`）；70 个字形、无空 charstring、charset 是合法的 format 1、
  `hmtx`/`maxp` 的字形数与之一致。
- 把 Private DICT 换成最小版本后 Skia 仍拒绝 → 也不是 Private DICT。
- 同一份程序 Peach（FreeType 路径）能正常加载，PDFium 也能。

**结论：这是 SkiaSharp 的 CFF 解析器与 FreeType/PDFium 的兼容性差异，不为它改 Skia。** 具体触发点没有
追到底（剩余候选是某个 charstring 操作码、或 charset 引用的 SID 越出 String INDEX），但对决策没有影响。

**顺带否掉的一个方案**：试过「回退字体改用自身字宽」，该文件 22.21% → 12.68% 看似变好，但墨迹掉到
2.42%（PDFium 10.66%）——回退字体里部分字符映射失败、字宽退化成 MissingWidth，字符全挤在起点，反而
更糟，已回退。字形是错的，排版就不可能有正确答案；按 PDF 宽度定位是其中更站得住的一方。

## 第十轮 —— `function_based_shading_cmyk`（22.75%）：CMYK 转换模型差异，不是 bug

页面上三块函数型着色（Type 1 + `/Size [2 2]` 采样函数 + DeviceCMYK）。看图两边**色相完全一致**
（白 → 青 → 洋红 → 黄），只是 Poso 整体略亮。逐点核对：

| 位置 | PDFium | Poso |
| --- | --- | --- |
| (u0,v0) 白 | (236,234,244) | (239,240,247) |
| (u1,v0) 青 | (0,176,225) | (1,188,231) |
| (u0,v1) 洋红 | (233,64,142) | (248,73,154) |
| (u1,v1) 黄 | (197,182,47) | (208,184,64) |

**根因**：Poso 的 `PosoColor.FromCmyk` 用的是 **pdf.js `DeviceCmykCS` 的多项式系数**（逐字对应
`src/core/colorspace.js:707` 起的那组常数），手算验证 `(0.774,0.014,0.106,0)` 得 (35,195,225)，实测
(37,191,220)——完全吻合。而 **PDFium 走 ICC/SWOP 查表**，是印刷级的颜色管理。

**所以这不是「PDFium 错」**：PDFium 的模型更接近真实印刷，pdf.js 的多项式是当年为了省一份 ICC 表做的
近似。但 pdf.js 是这个语料的参考实现，Poso 与它一致是有意为之（`FromCmyk` 的注释就写着这是「元素颜色
用的同一个转换」）。要改就得引入一份 CMYK ICC profile 并做查表插值，属于另一个量级的工程，**不做**。

顺带记一个查证过程中的坑：我一开始采样点落在抗锯齿边缘上，把黄角读成紫蓝，误以为 PDFium 把
`Size[2 2]` 的维度顺序搞反了。内缩 6% 后四角色相一致，那个「维度顺序」的怀疑不成立并已排除。

## 第十一轮 —— `issue7229` 页 0 残余 12.5%：文件本身损坏，不改

上一轮修好页树后两页都出来了，但页 0 还差 12.5%。查下来是**这张图的数据位置与声明不符**：

- 页 0 引用 `obj 1`，字典写 `/Length 806614`，但 `stream` 之后紧跟的是 **3454 字节的 XMP**
  （`<?xpacket ...`），然后是 `endstream`。
- 真正的 JPEG 在**偏移 176**（紧跟 `1 0 obj` 的对象头），长度正好 806614 ——`/Length` 本身是对的，
  是**数据放错了位置**。
- `807826 + 806614 = 1614440`，超出文件长度 `1153154`，所以 Poso 的 `/Length` 校验正确地拒绝了它，
  回退到搜索 `endstream`，于是拿到 XMP → 图像解码失败。
- 页 1 引用 `obj 9`，那是同一张图的完好副本（341094 字节），渲染正常。

**验证哪个才是 PDFium 画的**：把两段分别单独渲染整页再比——「obj 1 声称的 806614 字节」与 PDFium
的页 0 差 12.50%，「obj 9 的 341094 字节」差 28.63%。**PDFium 画的是前者**，即它靠扫描 SOI 把错位的
数据找了回来。

**结论：这是损坏文件 + 更激进的容错，与 `bug1308536` 同类，不改。** Poso 按 PDF 规定的 `/Length` 校验
后回退到 `endstream`，是规范行为；要在这种情况下去猜「跳过前导垃圾找 SOI」，属于为坏文件定制的启发式，
没有通用价值。

## 第十二轮 —— `issue7901` 修好了乱码（21.86% → 11.58%）

页面上是 "The Free Software Definition"，Poso 渲染成 `üäÍ†Ë çéÎŸ Ì{×í æ…`。

**根因**：这是个 `CIDFontType0`，嵌入的却是 **name-keyed CFF**（OTTO 容器里的 231 字形 CFF）。CID
字体按编码编号，CFF 按 charset 编号，两套顺序毫无关系——code 175 是 "T"，而 CFF 的 glyph 17 才是 "T"。
Poso 对没有 CID 空间的程序退回了「code 即 glyph id」，于是整行乱码。Peach 表现一致，说明问题在共用管线。

**修法两处**：

1. `PdfFont` 现在会从**真正的 OTTO 容器**里取出 CFF 表并解析——既有的提取器只处理不含 head 表的伪
   容器，完整容器被原样放过，从未贡献字形名。
2. `Type0PdfFont` 对「程序自身没有 CID 空间」的 CIDFontType0，走 **ToUnicode → 字符 → Adobe Glyph
   List → 字形名 → CFF charset → glyph id**。空格直接按字形名 `space` 取：这个文件的 ToUnicode 把
   code 32 映射成 U+00FE（þ），照它画会在每个词间断处冒出一个 þ。

结果：Skia 21.72% → **13.05%**，Peach 21.86% → **11.58%**，文字可读；`issue215` 也从 7.50% 降到 6.48%。
全语料零回归。

**这个修复没有随附单元测试**，如实说明：用同一字体构造的 fixture 在修复前也能正确渲染（走的不是同一
条 code 路径），测试会形同虚设。验证改在语料上做，失败形态记录在此。

**残留**：渲染里仍有一处多余字形（词间位置的一个十字形），来自 OTTO 容器内 gid 与 CFF 字形顺序的不一致，
与本次修复是两件事，未处理。

## 第十三轮 —— `issue19634`（20.95%）：Type3 + 嵌套软掩码，已定位未修

页面是五行 "guest"，每行由**两个 Type3 字体叠画**：F4 用**位图掩码**造字形（CharProc 里
`32 0 0 32 -128 -1312 cm` + `/G23 gs` + `0 0 42 61 re f`，`/G23` 是 `/SMask << /S /Luminosity /G form >>`，
form 里再引用一张 42×61 的灰度 JPEG）；F5 用**真正的贝塞尔路径**（`d1` + 一串 `c` + `h` + `f`）。

PDFium 显示绿色 = F5 的路径字（后画、盖住 F4）正常渲染。
Poso 显示淡红色块 = **只画出了 F4**（掩码图 mean=65，偏暗，所以红色被压淡），**F5 的路径字没有出现**。

已排除的：

- 掩码 JPEG 本身解码正常（托管/Skia/Peach 三方都能解出 48×57、42×61）。
- F5 的 CharProc 是完整的（以 `h f` 收尾，有填充）。
- F5 没有 `/Resources`，但它的 CharProc 不需要资源，`font.Resources ?? _currentResources` 的兜底成立。
- `d1` stencil 的颜色抑制只作用于 CharProc 内部的颜色操作，不涉及外部 `0 1 0 rg`。

**未修原因**：这个文件把 Type3、`d1` stencil、嵌套 Luminosity 软掩码（form 里再引 ExtGState）、
无 Resources 的字体四件事叠在一起，构造最小复现的成本远高于单个文件 20% 的收益。诊断结论记录在此，
下次若在真实业务文档里遇到同类组合可以直接接手。

## 第十四轮 —— `issue2177`：Peach 的斜切平铺图案（19.76% → 7.97%，已修）

这个文件 Skia 8.93%、**Peach 19.76%**，图上 Peach 的三个圆里图案碎成了零散小点。

**根因**：平铺图案的 `/Matrix` 是 `[1.4 1 -.5 .7 0 0]`（**斜切**）。解释器对斜切矩阵有专门处理，但前提是
后端声明 `SupportsTransformedImagePlacement`——否则每个格子被压平成轴对齐的包围矩形，斜切被丢掉，图案就
散成点。**Skia 声明了这个能力，PeachDrawing 从来没有**。

而 `PeachContext.DrawImage` 一直是**通过当前 CTM 做 blit** 的（`Blit(..., _ctm)`），能力本来就有，只是没
声明。补上这一行：Peach 19.76% → **7.97%**（三个实现里最好），`tiling_patterns_variations` 也从 5.37%
降到 3.34%，全语料零回归。

## 第十五轮 —— 未着色平铺图案的颜色（`issue16038`、`tiling_patterns_variations`）

`issue16038` 是网格填充：`[/Pattern /DeviceRGB] cs` 选中 `pgfprgb`，然后 `0 0 1 /pgfpat21 scn`，
图案格子内容只有一句 `0.3985 w ... l S`（描一条线）。**PaintType 2 的图案本身不带颜色**，颜色来自
`scn` 放在图案名之前的操作数，所属空间是 Pattern 空间的 base——而 Poso 两处都没读：`[/Pattern <base>]`
的 base 被丢掉，`PaintType` 更是从未被检查，于是格子用默认黑色描线，整页网格变成灰色。

**修法**：Pattern 空间记住 base 类型；`SelectPattern` 在弹出图案名之后取剩下的颜色操作数；`RenderCell`
用它初始化格子的填充与描边色。

结果：线变成蓝色。`issue16038` 本身只从 35.07% 降到 33.16%，因为剩下的差异是**线宽**——2 倍缩放下，
2.99pt 格子里的 0.4pt 线不足一个像素，两个引擎抗锯齿方式不同。真正的收益在专用测试文件
`tiling_patterns_variations`：**3.34% → 0.21%**（两个后端），全语料零回归。

## 第十六轮 —— `copy_paste_ligatures`（23.54%）：小字号抗锯齿，不是 bug

缩略图上 Poso 的连字（ff、fi、fl、ffi、ffl）看起来像拆开的独立字母，放大后发现**字形完全一致**，两边
都是正确的连字。逐列统计差异均匀分布在全行（17–30%），没有错位。

判据与前几轮一致：**放大后收敛**。同一文件单独渲染，2 倍 17.01%、**8 倍 3.72%**——典型的 12pt 小字在
低分辨率下的抗锯齿差异，没有可修的语义错误。

## 第十七轮 —— `text_clip_cff_cid`（Peach 19.04% / Skia 0.22%）：Peach 的 CID 字形间距，已定位未修

这个文件的 Skia 只差 **0.22%**，Peach 却 19.04%——是「Peach 明显输给 Skia」的文件里最严重的一个。
注意 triage 清单按两者最大值排序，所以它显示 19.04%，容易误读成双后端都错。

**已排除**：不是文本裁剪（`7 Tr`）的问题。把同一个 CID-keyed CFF 字体单独渲染、不用裁剪模式，Peach 依然
把 "ABC123" 挤成一团（墨迹 90655 vs Skia 34416）。也排除了 CID→GID 映射——探针显示
`CID 68 → gid 24 ("A")`、宽度 0.707，与 `/W` 一致。

**已定位到**：Peach 字形**间距**偏小（字形自身形状正确）。嫌疑在 `glyph.X`（em 单位）与
`PeachGraphicsContext.AddOutline` 的 `EmToPoint` 之间——那里算的是
`originX + x * scale`，`x * scale` 是设计单位折成 em，而 `originX` 若已是 em 就不能再参与同一套换算。
未继续的原因是 `UnitsPerEm` 在两个后端的取值需要更细的插桩，而本轮上下文已到上限。

**顺带**：已把 `ClippingOnlyText_ClipsTheFillAfterTheTextObject` 从只跑 Skia 改成 Theory 覆盖两个后端
（`3c9e105`），两个都通过——说明简单场景下 Peach 的文本裁剪是对的，问题确实在字体而非裁剪。

## 第十八轮 —— 「Skia 落后」那三个：全部不是 Poso 的语义问题

按上一轮的清单，先啃「Peach 反而更好」的三个，结论是三个都不需要改代码：

- **`bug1743245`（报告 11.48%，单独渲染仅 4.33%）**：页面背景是网格，由几千条 **0.5pt 描边线**构成，
  外层 CTM 缩放 0.317 后每条线只有 **0.16 设备像素**宽。Skia 全程开抗锯齿，覆盖率 0.16 的线必然被稀释
  到约 217（数学上正确），而 PDFium 和 Peach 对亚像素线做了最小宽度钳制，落在 133/142。**两种策略都
  合理**，Skia 的更"正确"但视觉上网格几乎消失。放大到 8 倍后收敛到 **0.91%**。
- **`issue12399_reduced`（Skia 9.88% / Peach 3.23%）**：Skia 报 `FontFallback`——内嵌的裸 CFF
  （`/Type1C`，59 字形）SkiaSharp 加载失败、回退到系统 Times New Roman 后仍用 PDF 的 `/Widths`，
  于是字符挤成乱码；Peach 正常加载，文字完全正确（"Consolidated financial statements"）。
  用 `CffOpenType.Build` 包装后喂给 `SKTypeface.FromData` 直接返回 null，而另外两份 CFF
  （`stonesans` 13415 字节、`issue7901` 的 34995 字节）同一路径都能加载 → **这是 SkiaSharp 的 CFF
  解析器对特定程序不兼容，与 `bug1308536` 同类**，不为它改 Skia。
- **`issue4061`（Skia 8.62% / Peach 2.71%）**：CJK 文字 "轉寄：針對港鐵幕門安全性的投訴"，三者文字
  完全相同，只是字重抗锯齿差异。8 倍下收敛到 **1.38%**。

**顺带改了报告排序**：triage 清单现在同时给出 `skia_pct`、`peach_pct` 和 `worse` 三列。原来看最大值的
排序会把 `text_clip_cff_cid`（Skia 0.22%、Peach 19.04%）排到第 8 位并让人以为是共用缺陷，实际只是
Peach 一个后端的问题——我这轮就因此先查错了方向。

## 第十九轮 —— `text_clip_cff_cid` 收尾：根因在 PeachText 内部

> **第二十七轮更正**：「根因在 PeachText 内部」只对了一半。真正原因是 CID 键控 font dict 用
> **CharMatrix（12 7）** 声明缩放，而 Poso 的读取器只找 FontMatrix（7），落到 1/1000 默认值——
> Peach 的 2× 来自容器 upem 错、Poso 自己的轮廓解析同样 2×（当时的表里「FontMatrix
> `[0.001,0,0,0,0.001,0]`」其实已经是布局错的读数）。修复见第二十七轮，Peach 19.04% → 0.88%。

上一轮定位到「Peach 的字形间距偏小」。这一轮把它钉死到具体数字：

对同一个 gid 24，两个后端拿到的轮廓包围盒是
**Skia `(0.019, 0, 0.645, 0.707)`** 与 **Peach `(0.039, 0, 1.322, 1.448)`** —— Peach 恰好是 **2 倍大**。
字形被放大到两个 em 宽，自然互相重叠，看起来就像"字距太小"。

**已逐项排除**：

| 检查 | 结果 |
| --- | --- |
| CID→GID 映射 | 正确（CID 68 → gid 24 "A"，宽度 0.707 匹配 `/W`） |
| `CidCffProgram` 的 FontMatrix | `[0.001,0,0,0,0.001,0]`，upem 1000，正确 |
| 容器 `head.unitsPerEm` | 1000，正确 |
| 容器 `hmtx` 的占位 advance | 改成 `upem` 后 Peach 仍 19.04%，**不是它** |
| `head` 的 FontBBox（该字体是 `[-300,-410,2045,1800]`，超出 em 范围） | 按 upem 归一化后 Peach 仍 19.04%，**不是它** |

**结论：PeachText 解析这个 CID-keyed CFF 时，把轮廓按约 2 倍的尺度展开。** PeachDrawing 是第三方
NuGet 包（无源码），Poso 侧没有干净的修法——强行检测"轮廓是否过大"再纠正属于给一个库打补丁，会波及其它
字体。与 `bug1308536` / `issue12399_reduced` 的 SkiaSharp CFF 问题同一处置：**记录，不改**。

**这也修正了上一轮的措辞**：不是"字距太小"，而是"字形放大到 2 倍导致重叠"。

## 第二十轮 —— 双后端一致的 147 个：抽样分类

从 147 个「两个后端都差 ≥5%」的文件里随机抽 14 个，按内容特征归类：

- **字体类占约 10 个**（Type1 6 个、Type0 4 个），其中 4 个是同源的 `tracemonkey` 系列。
- 其余为 shading、xObject、可选内容。

用放大判据筛出真正的结构性差异：

| 文件 | 2× | 8× | 判断 |
| --- | --- | --- | --- |
| `tracemonkey` | 7.81% | **1.64%** | 收敛 → 抗锯齿，非 bug |
| `issue6721_reduced` | 7.57% | 5.79% | 部分收敛 → 混合，暂不追 |
| `issue15716` | 14.72% | **13.04%** | **不收敛** → 结构性 |

`issue15716` 是 ♠♥♦♣ 四色图案格：两边形状一致，但 **Skia 的字形更大、填充更满**。这来自
ZapfDingbats 的替代字体（`Segoe UI Symbol`）与 PDFium 内置映射的字形轮廓差异——**替代字体的字形
天然不同**，和 `bug1308536` 同类，无解。注意这个文件此前已从 32.75% 修到 15.46%（dingbat 映射那次），
剩余的 13% 是字形本身的差异。

**结论**：147 个里抽样到的绝大多数是抗锯齿或替代字体差异，没有发现「一次能修一批」的共同语义缺陷。

## 附：单元测试的 Peach 抖动（一直存在，与本轮改动无关）

`dotnet test` 全量运行时常有 1 个失败，且**每次失败的具体测试不同**（已见过
`OddLengthDashArray_StillStrokesTheLine`、`ExtGStateMaskOnATransparencyGroup_SurvivesTheFormClearingIt`、
`ShadingPatternStroke_OnAnOffsetMaskedLayer_LandsWhereItWasClipped`），单独运行则全部通过。
三次连续全量运行：失败 1、通过 0、失败 1。

这与第五轮记录的 Peach 跨文档状态问题同源（`PeachFont.SharedFontSet` 进程级缓存 + xUnit 并行），
**在本轮任何改动之前就存在**（曾用 stash 掉源码改动的方式验证过）。判读方式：看失败的是不是 Peach 参数化
的用例、单独跑是否通过；若都成立即为抖动，不是回归。

## 第二十一轮 —— 把「打不开」的 20 个文件清到 15 个（修了 5 个真崩溃）

这一轮换了个角度：不看像素差异，看**PDFium 能打开而 Poso 直接崩溃或拒绝**的文件——那 9 个才是用户真正
会撞上的（打开就是空白）。修好 5 个：

| 文件 | 原因 | 修复后 |
| --- | --- | --- |
| `issue1985` | `/Indexed` 调色板短于一个条目，`maxIndex = -1`，`Math.Clamp` 在 min > max 时抛异常 | 2.69% |
| `operator_list_cycle` | xref 偏移超出文件末尾，`PdfLexer` 的 `_pos` 未钳制 → `IndexOutOfRangeException` | **0.00%** |
| `GHOSTSCRIPT-698804-1-fuzzed` | xref 子段写 `0 2` 却列了 7 条，catalog 未入索引 | **0.00%** |
| `issue18986` | 同上 | **0.00%** |
| `PDFBOX-3148-2-fuzzed` | 同上 | **0.00%** |

**前两个是纯粹的崩溃缺陷**（不是容错问题）：一个 `Math.Clamp` 的边界前提没检查，一个 lexer 构造没钳制
起点。后三个共用同一个根因：**表能产出 trailer，却产不出 catalog**，而既有的重建兜底只检查「零条目」和
「没有 /Root」两种情况，没再往前走一步。

修法：`PdfDocument` 在 xref 赋值之后校验「catalog 可达，且它指的 `/Pages` 可解析」，失败就重建表。
**这个位置很关键**——放在 `PdfXref.Load` 里会失败，因为解析对象本身要经过正在构建的 xref，结果每个健康
文档都被判为损坏（两个既有测试断言 `WasRebuilt == false`，正是它们抓到了我第一版的错误）。

**剩余 15 个打不开的**：`issue9418`（重建后 `/Pages` 仍解析不出）、`issue11549_reduced`（
`Dictionary keys must be names`）、`PDFBOX-4352-0`（`Unexpected keyword 'E'`）、`issue19517`（MediaBox
12608×16806，2 倍下 8.5 亿像素，非缺陷），其余 11 个 PDFium 也拒绝（损坏或加密）。

**这个修复没有随附单元测试**，如实说明：用 `CreateSimplePdf` 构造「子段少报条目」的 fixture 在无修复时
也能正常解析，测试会形同虚设。验证在语料上做。

## 第二十二轮 —— 打不开的文件从 20 清到 12

继续上一轮的方向，又修好 3 个：

| 文件 | 原因 | 修复后 |
| --- | --- | --- |
| `issue11549_reduced` | 字典里有一个关键字占据了「键」的位置，`ParseDictionary` 直接抛异常 | 4.74% / 3.74% |
| `issue9418` | 增量更新把 `/Root` 改写成了 `/Info` 字典——它也是字典所以能解析，但没有 `/Pages` | 1.65% / 0.37% |
| `PDFBOX-3148-2-fuzzed` / `GHOSTSCRIPT` / `issue18986` | （上一轮）catalog 不可达 | 0.00% |

**`issue9418` 那一处值得记下**：重建扫描其实**已经**能找到真正的 catalog（`catalogObjectNumber`），但只在
`/Root` **缺失**时才替换。而这份文件的 `/Root` 存在、指向一个字典（`/Info`），于是「能解析、但没有
`/Pages`」。修法是把「`/Root` 指向的不是 catalog」也当作缺失——重建路径和文档级检查两处都加。

**当前语料状态**：Skia 可渲染 **965/977**（本轮开始时 957），不可渲染 12 个；PDFium 也只开 954 个。

**还在的 12 个**：`issue19517`（MediaBox 12608×16806，2 倍下 8.5 亿像素，分配失败——不是缺陷，是测试
用 2 倍渲染巨幅页面）、`PDFBOX-4352-0`（加密文件，`6 0 obj` 的对象体损坏成 `E< /CF ...`），其余 10 个
PDFium 同样拒绝（损坏或加密）。

## 第二十三轮 —— 后端拒绝的字体，改用内嵌轮廓按普通 drawpath 画

语料里有 **14 个文件**的后端报告 `FontFallback`：内嵌字形程序加载失败，文字画成了系统替代字体。
此前这类文件一律「记录，不改」（第九、十八、二十轮），因为替代字体的字形和 PDF 要的字形天然不同。

这轮把兜底做了：Poso 自己解析内嵌程序——CFF 走新写的 Type 2 charstring 解释器（名称键控与 CID 键控、
局部/全局子程序、flex 族、seac 复合、hintmask 步进），TrueType 走 `glyf` 读取器（short/long loca、
二次曲线升三次、复合字形）——后端报替代时，把字形轮廓按 `/Widths` 给出的笔位用普通 `FillPath` 画。
门控在后端的 `UsedFallback` 上：后端能加载的字体一行代码都不经过它。

全量 977 个文件里**只有 6 个渲染发生变化**，其余 971 个逐字节一致：

| 文件 | 之前 | 之后 | 说明 |
| --- | --- | --- | --- |
| `bug1308536` | 23.58% | **14.13%** | Skia 拒绝的 Type1C 子集，轮廓画回来了 |
| `issue8480`（Peach） | 10.11% | **8.00%** | |
| `issue12399_reduced` | 11.41% | **10.49%** | |
| `bug1539074.1` / `issue7544` / `issue11768_reduced` | 0.59 / 0.12 / 1.93 | 0.60 / 0.12 / 2.02 | 抗锯齿级噪声 |

`issue11768_reduced` 那 0.09% 不是 Poso 的回归：PDFium 对那个 JIS-UTF8 CID 字体画的是 **.notdef 方块**
（oracle 自己没渲染出来），Poso 画出真正的字形反而「更不像」它。

过程中挖出并单独修掉一个真 bug：`Type1ToCff` 写负的 CFF bcd real 时用了 `0xC`（「E-」指数标记）而不是
`0xE`（负号），读者把 `-0.004` 解析成 0。pdf.js `bug1539074.1.pdf` 的第二个字体 FontMatrix 是
`[0.001 0.002 0.003 -0.004 0 0]`，这个 0 把所有 y 坐标压平到基线，字形变成横贯整页的黑楔。

**第九轮「不改」的结论就此推翻**：`bug1308536` 从 22% 档降到 14%，剩下的差距是嵌入子集字形与
PDFium 所用字形本身的差异，已经不是后端能力问题了。

## 第二十四轮 —— 兜底机制的两个「上游」修复：CFF 头尺寸与 .pfa 体

顺着上一轮的兜底继续查剩余 fallback 文件，发现两个更基本的解析 bug——修好后后端能直接加载程序，
连兜底都不再需要：

1. **CFF 头尺寸硬编码 4**（`79ae542`）。三个 CFF 读取器都从固定偏移 4 开始走 INDEX 表，而头部的第三个
   字节声明的才是头尺寸。pdf.js `issue13147.pdf` 的 CID 子集写的是 **12**，于是程序被解析成垃圾、
   CID→GID 映射从未建立、CJK 文本画成替代字体。修复后 **16.09% → 9.75%**（两个后端），
   「準会場で受験の方」字形正确。
2. **.pfa 的 eexec 体是 ASCIIHex 文本**（`20ecea6`）。`.pfa` 形式的 Type 1 程序把 eexec 密文写成十六
   进制字符（直到 `>`），Poso 直接把这些字符当密文解密，得到乱码。现在按前导字节的 hex 特征识别并
   先解码。`issue11740_reduced`（俄语「Оглавление」）**10.95% → 8.25%**，顺带 `issue4630`
   （Verdana .pfa）**2.99% → 1.82%**。

识别方法：真正的密文以 4 字节随机 IV 开头，约 94% 的概率落在非 hex 字符上，采样前导 64 个字符即可
区分。

**全量 A/B**：977 个文件里恰好 6 个渲染变化（3 个文件 × 2 后端），**全部改善，零回归**；331 个测试全绿。

**14 个 fallback 文件的现状**：`issue13147`、`issue11740_reduced`、`issue4630` 已经不再报 fallback；
其余的残余差距是嵌入子集字形与替代/参考字形之间的真实差异。`issue11740_reduced` 修后仍剩 8.25%：
字形已对，但字距偏大（「О лаг венв」对「Оглавление」），出在 Type0/CIDFontType0 的 CID 编号与转换后
CFF charset 的对齐上，记录在案，未修。

## 第二十六轮 —— 嵌入字体不再永久驻留共享 FontSet（内存生命周期）

`PeachFont.SharedFontSet` 是进程级静态，而 PeachDrawing 的 `FontSet` **没有移除接口**：`AddData` 注册进去的程序
（字节、family 记录、descriptor）会活到 set 本身销毁为止。过去每个文档的每个嵌入字形都注册进这一个 set，
于是对长跑的查看器/服务而言是一条无上限的泄漏。

修法：每个嵌入字体改注册到**自己私有的 `FontSet`**，set 随 `PeachFont` 一起被回收。三点探测支撑了这个设计——
① 新建 `FontSet` 构造 <1ms，且仍能看到进程级系统字族（`TryFindFamily("Arial")` 成功，因为那是只读系统库）；
② `AddData` 只写实例字段，不碰静态的 `InstalledFamilyNames`，也不被别的实例看到；
③ `Typeface` 一旦匹配即**自包含**，脱离创建它的 set 后仍能正常取字形/度量。共享 set 从此只做系统字族查询、
永不 `AddData`。Skia 侧无需对应改动：`SKTypeface.FromData` 本就不注册进任何集合，typeface 随 `SkiaFont.Dispose`
释放。回归测试 `PeachFontLifetimeTests` 直接读 PeachDrawing 内部字段，钉住「加载+释放 20 个嵌入字体后共享 set
的自定义家族数仍为 0」；stash 掉修复后这两个测试会失败。全量 1929 个渲染逐字节不变——只改生命周期。

**一处需要更正的旧归因**：第五轮把「Peach 数字依赖上下文」的串扰归到 `SharedFontSet` 的累积上。本轮用
文档记录的复现（`bug900822.pdf` 后渲染 `ZapfDingbats.pdf`）做了对照：修复前后跨进程哈希**完全一致**
（单独 `2A4A…`、前置后 `7668…`）。也就是说串扰**不在** Poso 注册进的那个 set，而在 PeachDrawing.Text 自己的
静态按名缓存里（`FontFactory.FontSourcesByName/ByKey`、`TypefaceCache` 单例），Poso 侧无法清除。本修复解决的是
内存驻留，不是串扰——后者仍按第五轮的判读方式（Peach 参数化用例、单跑通过即视为抖动）处理。

**抖动统计**：修复前约 2/3 的套件运行出现 1 个失败（每次测试不同）；修复后 20 次运行里 19 绿 1 红。
显著下降但未归零——与归因一致：`AddData` 的锁内写/锁外读竞争被消除了，但系统字体解析仍会写
PeachDrawing.Text 的静态缓存，那部分 Poso 碰不到。

## 第二十七轮 —— `text_clip_cff_cid` 的真凶：CID font dict 的 CharMatrix（12 7）

第二十六轮说过这个文件的 2× 是 PeachText 内部问题、Poso 修不动。这一轮直接证伪了自己：
**Poso 自己的轮廓解析器读出的是同一个 2×**（probe：gid 24 后端 Skia 0.645×0.707、Peach 1.322×1.448，
而 Poso 的 `CffOutlines` 也是 1.327）。手工解码 charstring 的坐标范围 ~(66,34)-(1265,1349) 设计单位，
1349/2048 ≈ 0.66 em 与 Skia 吻合——**1/2048 才是对的**。

根因：CID 键控的 font dict 用 **CharMatrix（算子 12 7）** 声明缩放，而不是名称键控程序的 FontMatrix（7）。
`CidCffProgram` 和 `CffOutlines` 都只找 7，落到 1/1000 默认值，于是：容器 upem 错 → Peach 归一化 2×；
轮廓矩阵错 → Poso 兜底解析也 2×。修复（`57120b8`）：新增 `CffMatrix.Declared`，按 FD 的 12 7 → 7 →
top 的 12 7 → 7 顺序解析，CID 读取器和轮廓读取器共用。

**一个必须防的坑**：identity 矩阵在这里是占位符而不是声明。`issue11713.pdf` 的 FD 写着
`[1 0 0 1 0 0]`，而它 top DICT 的 12 7 才是真正的 1/1000——照单全收会把 upem 设成 1、字形炸到一千 em
宽（第一版修复就把 issue11713 从 0.12% 打到 45.18%，就是踩了这个坑）。`CffMatrix` 跳过 identity 继续
往上找，与 FreeType 的「plausible matrix」守卫殊途同归。

**结果**：`text_clip_cff_cid` Peach **19.04% → 0.88%**（Skia 一直 0.22%，它自己解析 CFF 时读对了 12 7）。
全量 A/B：与第二十六轮基线相比只有这一个文件变化——identity 守卫把中途一度出现的 3 个回归
（issue11713/issue16500/issue3061）全部消掉了。三个新测试钉住：FD CharMatrix 生效、identity 被跳过、
轮廓尺度落在 em 内。

**归因链的教训**：同一个 2×，第十九轮归给「PeachText 内部」、第二十六轮维持、第二十七轮才发现两边
各有一份同样的 bug。第三方库的错不排除我们自己的错——两边读数一致地错时，正确的那一方（Skia）才是
线索。

## 第二十八轮 —— TrueType 兜底：字形只能经 sfnt 自己的 cmap 找到

兜底机制上线时（第二十三轮），`ResolveOutlineGlyph` 只按字形名找轮廓：Type1/Type1C 的名称键控
程序没有问题，但简单 TrueType 字体没有字形名——PDF 码经程序内部的 encoding 直接映射到 glyph id，
重新编号的子集里这个 encoding 只有程序自己知道。于是 PeachDrawing 拒绝的 TrueType 程序（如
pdf.js bug1200096.pdf 的 Times 子集）每个 code 都返回 -1，兜底什么都画不出来，Peach 一直用
替代字体。

修复（`39c66a4`）：`TrueTypeOutlines` 读 sfnt 的 `cmap`（format 0/2/4/6/12；format 2 是 8/16 位
混合表，bug1200096 的子集正是这种），`SimplePdfFont` 在名字路线失败后把 code 的 Unicode 值送进
cmap。验证：被 Peach 拒绝的 bug1200096 子集，code 65 → gid 36，'A' 的轮廓宽 0.70 em、底边贴基线。

**数字上几乎中性**：全量 A/B 只有两个 Peach 渲染变化——bug1200096 8.63% → 8.62%、
issue14117 5.82% → 5.84%。因为 Peach 原先拿到的替代字体恰好也是 Times 家族，形状本来就接近；
修复的意义在于画的是文档自己的字形，而不是碰巧长得像的替代品。

新测试：TrueType 读取器直接采样 gid 30..44（要求 ≥10 个有轮廓且互不相同），以及 Peach 后端的
端到端测试（程序强制 UsedFallback → NeedsOutlineFallback → code 65 出轮廓 → 页面有墨迹）。

## 第二十九轮 —— 加密字典读不出来，文档不再整个打不开

重建基线时把「完全渲染不出页面」的旧清单（20 个）逐条重跑，发现其中 8 个早在之前几轮就修好了，
清单一直是陈旧的。真正剩下的头号问题是 pdf.js `PDFBOX-4352-0.pdf`：它的 `/Encrypt` 对象开头被 fuzz
成 `E<` 而不是 `<<`，`ResolveEncryptionDictionary` 里 `Resolve` 抛出 `PdfFormatException` 直接冲出
`PdfDocument` 构造函数，整份文档被拒绝。PDFium 能打开，画出一页空白——因为内容流确实是 AES 密文、
谁也解不开，但「打不开」和「空白页」是两回事。

修复（`9a57ad6`）：读不出的字典等同于没有字典，文档按未加密打开，每条流按自身过滤器能恢复多少画
多少；对象头扫描那条兜底路径也加了同样的保护。语料库中只有这一个文件变化：从无页面变成 400x100
渲染，与 PDFium 参照逐像素一致（全白）。新测试钉住「打开且保持空白，而不是把密文当明文画出来」。
poppler-742 / poppler-937 也报 `Unexpected keyword`，但错在各自的内容流，PDFium 同样拒绝，未改。

**重建后的语料库数字**（`out2/` 基线，`report_new.tsv`）：Skia 成功 966（缺 11）、Peach 成功 965
（缺 12），比旧基线的 957/955 明显回升；Peach 侧因兜底与字体修复在一批文件上大幅改善：
`franz_2` 97.31% → 2.83%、`issue12841_reduced` 82.88% → 0.27%、`endchar` 32.55% → 5.34%、
`text_clip_cff_cid` 19.04% → 0.05%、`issue15716` 32.75% → 15.46%、`issue2177` 19.76% → 7.97%。
唯一变差的是 `jpx_smaskindata` 46.07% → 54.38%：`429cb18` 起 Poso 按 pdf.js 的做法对预乘的 JPX
采样做 un-premultiply，而 PDFium 忽略 `/SMaskInData` 直接按预乘值不透明绘制——这是已记录的参照
分歧，不是回归。`bug1802506` 的 Peach 数字上升同样是标注外观流（PDFium 不画标注，见
`oracle-annots`）。

**重建还暴露出一个 Peach 独有的失败**：`issue3405r` 在 Skia 下正常、Peach 下整个页面抛
`OverflowException`。它内嵌的 WenQuanYi 程序头声明 9 张表，而目录只有 8 项——第 9 个「表项」其实是
`head` 表的前 16 字节（tag 为 `\0\1\0\0`，offset 17 亿、length 15 亿）。`RepairSubset` 把超界表项的
长度截到 `data.Length - offset`（负数），累计总长变负，`new byte[total]` 溢出。修复（`c761fb8`）：
范围不在程序内的表项直接丢弃，而不是截断。修复后 Peach 渲染出 400x100，均值 232.8 对 PDFium 的
232.5。Peach 的无页面数因此也是 11，与 Skia 一致。


## 第三十轮 —— 全白文件清扫：11 个变 5 个，外加图案定位

第二十九轮重建基线时发现「全白」清单一直被百分比掩盖：小面积内容整块缺失，差异只有 3–10%，
排序里永远沉底。这一轮按根因逐个修：

1. **TJ 数组里的杂散 token**（`bug1953099`）—— 数组里有一个孤立的 `-`，`PdfArray` 解析抛错，内容
   循环吞掉异常后把剩余元素当零散操作数，TJ 拿不到数组，整行消失。修复（`74e0960`）：数组里的杂散
   token 只损失该 token。页面从全白到 1.55%。
2. **gid 0 是字形而不是「没有字形」**（`issue8234`）—— 该子集的 cmap 把 code 32 映射到 gid 0，而它的
   gid 0 是一个复合字形。Poso 把 gid 0 跳过，PDFium/MuPDF 照常绘制 .notdef。修复（`2030206`）：
   所有字形都交给后端。1.23% → 0.02%。
3. **页面资源里没有该字体**（`issue5954`）—— /Resources 只有空的 /XObject，正文却用 /F1。PDFium
   用内置 Helvetica 画，Poso 直接丢弃。现在同样补一个标准字体。空白 → 2.4–2.9%。
4. **Peach 的 cmap 被空表遮蔽**（`issue2017r`、`bug1027533`、`issue4800`、`bug1151216`、`issue7020`、
   `visibility_expressions`）—— `RepairSubset` 补的 (3,1) 空表在 FreeType 的查找里优先级最高，把
   (3,0)/(1,0)/(0,1) 的真实映射盖掉，所有 code 都成 gid 0。修复（`76bbd67`）：新增共用的
   `SfntCmap`，按 (3,10)→(3,1)→(0,*)→(3,0)→(1,0) 合并、跳过 0、把符号表的 0xF0xx 归一到低字节；
   PeachFont 优先使用程序自己的映射。六页回到 2.6–4.6%。
5. **非嵌入 CJK 字体**（`issue8372`、`issue13343`）—— 修法见 `3b39791`：预定义 CMap 的字节布局与
   Unicode、按 ordering 选择系统 CJK 字体。代价是替代字形的宽度/形状与 PDFium 不同，所以数字反而
   变大（2.03%→3.62%、3.99%→6.84%），但页面从空白变成正确文字（目录 / (57)【要約】）。
6. **平铺图案定位**（`issue13561_reduced`）—— 见 `70df055`：格点从 BBox 原点排、图案锚定在内容流
   入口变换、镜像矩阵走变换路径。从空白到 0.88%（墨迹 0.77% 对 PDFium 0.76%）。
7. **损坏的 cmap 偏移**（`bug1050040`）—— 子表偏移按 uint 加到表头后可回绕成负数，旧的
   `offset + 2 > length` 判断放行，读越界让 Peach 整页抛异常。修复（`1ff794b`）：越界记录跳过。

**最终数字**：两个后端都 967/977 成功（缺 10），Peach 与 Skia 的无页面集合一致；「Poso 全白、
PDFium 有内容」剩各 5 个。剩余两类已知缺口没有修：`rc_annotation`（Text 便签注释，PDFium 合成图标）、
`bug1865341`（FreeText 注释没有 /AP，需按 /DA 合成外观），以及 `bitmap-symbol-symhuffrefine*`
3 个（JBIG2 的 Huffman 符号字典 + 符号细化，解码器目前返回 null）。这三类都是功能级缺口，已在
`Jbig2/Unverified.txt` 与本节记录。


## 第三十一轮 —— 按墨迹比例分诊：又五处内容缺失

全白文件清完后，用「墨迹不足 PDFium 60%」筛出 27 个未标注文件，逐个看结构。修好五处：

1. **编码后的图像掩码**（`issue4379`）—— 只有 DCT/JPX 掩码走图像管线，CCITT 掩码被当作原始样本读，
   1000×800 的索引图变成顶部一条噪声。修复（`94350f8`）：CCITT/JBIG2 掩码也走图像管线，覆盖率用
   白色模板读出以保证 alpha 在预乘后仍可读。0.63% → 0.00%（逐像素一致）。
2. **Form XObject 继承调用方资源**（`issue1055r`）—— 无 /Resources 的 Form 按 ISO 32000-1 表 95
   继承调用处资源；Poso 传 null，嵌套的 Im14 找不到。9.09% → 2.34%。
3. **Type3 字形过程保留调用方文本矩阵**（`pr4922`）—— charproc 里的 `Tm` 覆盖了外层文本矩阵，第一个
   字形之后全部从字形原点定位。6.38% → 3.63%（Peach 2.51%）。
4. **符号 TrueType 按码值走程序 cmap**（`issue8229`）—— 子集只有 (1,0) 格式 6 表，按 PDF 码索引；
   ToUnicode 说的 'R' 不在表里。后端和字符查找都失败后，再用码值查程序自己的映射。
   10.57% → 4.31%。
5. **`g<glyph id>` 字形名**（`issue13316_reduced`）—— YouYuan 子集用 /g5167 这类名字，AGL 不认识、
   子集没有 post 表；PDFium 把数字当字形号。Skia 7.93% → 4.23%，Peach 7.94% → 1.48%。
   另外 `canvas-464d32` 因第 2 条从 5.95% → 4.32%。

全量 A/B（`out6`）：**零回归**，五个文件改善。仍未处理的：

- `issue14462_reduced` —— 内嵌 Type1 程序本身被截断（fontTools 都拒绝解析），名字表尾部是乱码；
  各渲染器都在猜，未改。
- `issue14999_reduced` —— Type3 字形是内联 CCITT 图像遮罩，Poso 画出但被压成细条（Type3 的
  负 y FontMatrix 与内联图像放置的组合），未定位完。
- `issue15441` —— 非嵌入 Identity CID 且无 ToUnicode，CID 就是原字体字形号；PDFium/Skia 恰好能画，
  Peach 的替代字体字形表不同。没有「正确」答案，不追。


## 第三十二轮 —— JBIG2 的 Huffman 符号细化与聚合

`bitmap-symbol-symhuffrefine*` 三个文件一直是「Poso 全白、PDFium 有 7.1% 墨迹」的代表。根因在符号
字典段：`SDHUFF=1, SDREFAGG=1` 时解码器直接返回 null。这一轮按 PDFium 的 `JBig2_SddProc` 补上两条
构造（`fd7436b`）：

- **单实例细化**：用 `SBSYMCODELEN` 个原始位给出参考符号号，RDX/RDY 走表 B.15，细化位图的字节数
  （BMSIZE）走表 B.1；位图用细化上下文解码后，Huffman 流跳过 BMSIZE 字节。
- **多实例聚合**：它是一段以「已导入符号 + 已解码符号」为符号集的文本区域。与文本区域段不同，
  流里**没有符号码表**：PDFium 用定宽（`ceil(log2(SBNUMSYMS))` 位）的恒等编号，随后实例数据走
  B.11/B.6/B.8、每实例细化走 B.15/B.1。放置主体从 Huffman 文本区域解码器里抽出来共用。

结果：`symhuffrefineone` 与 PDFium **逐像素一致**（7.24% → 0.00%）；`symhuffrefineseveral`
7.24% → 0.29%（421/159,600 像素，聚合里一个 23×23 字形框的放置仍与 PDFium 不同，测试里登记为
near-match）；`symhuffrefine-textrefine` 仍降级为 null（它的聚合把细化交错在另一种布局里，暂未
跟上）。其余 74 个 JBIG2 向量不变。

**顺带修正第三十一轮的一个误伤**（`276a9c2`）：`g<glyph id>` 约定把 `bug1027533` 的 `/g0024`、
`/g003a` 也当成了字形号，而它们是前导零的字符码。现在带前导零的名字走正常字符/Unicode 路径，
该文件回到 Skia 3.14% / Peach 2.74%，`issue13316_reduced` 的修复保持不变。

**最终数字**：Skia 与 Peach 都 967/977，可比 953 个文件；82.4% 在 5% 以内、98.7% 在 20% 以内；
「全白」剩各 3 个（`symhuffrefine-textrefine`、`rc_annotation`、`bug1865341`）。


## 第三十三轮 —— 注释外观合成（Ink / FreeText）与 Peach 的 cmap 能力位

「全白」清单里的两个注释文件：`bug1865341`（FreeText 无 `/AP`）与 `annotation-ink-without-appearance`
（Ink 无 `/AP`）。两者都不是必须带外观流的类型，缺 `/AP` 时被整条丢弃。

- **Ink**：几何在 `/InkList`（每条折线一组交替 x/y），按 `/C` 颜色与 `/BS` 线宽圆角描边
  （`09bb667`）。0.41% → 0.25%。
- **FreeText**：`/DA` 给出字体/字号/颜色，`/Contents` 是正文（UTF-16 带 BOM 时按 UTF-16 解）。
  合成时把文字裁剪在 `/Rect` 内并画出 PDFium 同款边框；默认字体编码不了的字符丢弃——PDFium
  把 `Załącznik` 画成 `Zacznik` 也是这个原因。`bug1865341` 1.41% → 1.12%（Peach 1.08%），
  另一个 `freetext_no_appearance` 0.23% → 0.15%。

**顺带补的 Peach 缺口**（`8fb3144`）：`IFont.MapsCodePointsToGlyphs` 在 Peach 字体上取接口默认值
true，所以没有 cmap 的 TrueType 程序不会被替换，`TrueType_without_cmap` 在 Peach 上全白。改为按
原始程序的表目录检测（`RepairSubset` 会补一张合成 cmap，不能看修补后的数据）。该页 Peach 从空白
到 0.46%（Skia 0.41%），`issue14297` 的 Peach 差异 4.89% → 3.97%。

**仍未处理**：`rc_annotation` 的 Text 便签图标（PDFium 自绘的便签 chrome，非文档内容），以及
`symhuffrefine-textrefine`。本轮两个方向合计后「全白」只剩这 2 个。

**第三十五轮顺带查出两个待办**：

- `issue11279.pdf` 的页码「1」没画出来：字体的 `/CharSet` 只有 `/one /A /B`，**没有「1」**。PDFium
  用替代字形画了它，Poso 画不出——这是「字体缺字形时如何替代」的功能缺口（标准 14 的 CMR10 不含
  数字 1 时该用什么），与本轮的退化轮廓无关。
- `issue14999_reduced.pdf` 的 Type3 字形虽然位置修对了，每个字仍缺下半部分。它的 charproc 把内容
  缩放 0.01 倍后放进 10600 单位的格子，字形与图像的比例是 1:10000 这种病态情形，PDFium 与 MuPDF
  一致而我们不一致，说明我们对 Type3 字形空间的解释还有偏差。


## 第三十四轮 —— 退化轮廓：零高度的矩形画的是它的边

`issue4260_reduced.pdf` 整页是表格线，而每条线都是一个**零高度**的矩形（`848 1085 10159 0 re`）。零面积
的轮廓严格填充什么都不画，PDFium 却画出它的边——之前我们只画出了页面的外框。

修复（`416edc8`）：

- 退化的轮廓画成**一个设备像素宽**的线，并对齐到它落在的那个像素（不是画成描边路径：比一个像素细的
  描边会被抗锯齿成浅灰，不是 PDFium 画的实黑）。
- 只有**全由直线段构成**的轮廓才算退化：共线的曲线仍然是曲线，PDFium 不把它画成线。
- 退化轮廓与正常轮廓**混合在同一条路径**时整体不填——退化轮廓的绕数会抵消另一个。
- 只含一个 `m` 的子路径什么都不画，既不是形状也不是线，算成退化轮廓会抵消旁边的真实轮廓
  （`4a641d5`；`issue11279.pdf` 的 form 以 `h m f` 结尾，两个 SoftLight 圆因此整个消失）。

`issue4260_reduced` 4.04% → 1.83%，全量 A/B 零回归。

**顺带暴露的独立缺陷**：`issue11279.pdf` 的两个 SoftLight 圆（PDFium 66,324 像素）我们一个都没画出来
（48 像素）。这是 form 的透明组 + SoftLight 混合语义问题，与本轮无关，是下一个目标。


## 第三十五轮 —— Type3 字形过程要在字体空间里执行

Type3 字形过程的画布是 `FontMatrix × 文本空间 × CTM`（ISO 32000-1 9.5.5），但只有**画布**跟着这个变换，
解释器的图形状态仍是页面的矩阵。于是字形过程自己的算子（最要命的是 `cm`）量错了基准。

`issue19634.pdf` 的 F4 字形（d1 模板、按文字色填充的矩形）因此被画成**错位的大方块**拖在每行文字
后面，而不是 PDFium 那种灰色柔和的阴影。修复（`683abb5`）让图形状态跟着画布走，**19.20% → 6.74%**，
墨迹从 4453 像素降到 3669（PDFium 3841），阴影形状也对上了。

**这推翻了第二十七轮的仲裁结论**：当时把残余的 19.2% 判为「PDFium 离群」（依据 MuPDF 有 92% 的差异
像素站在 Poso 一边）。那个仲裁本身没错，但它掩盖了真正的原因——Poso 的阴影本来就画错了。

同一处改动让 `issue14999_reduced.pdf` 的字形位置正确了（此前整页文字被压成细条），但它仍缺每个字
的下半部分，原因尚未定位，记为待查。


## 第三十六轮 —— CFF 没有内置编码时按标准编码读；MMR 与 PDF 的 K 分开

**CFF 无内置编码**（`c5aa12c`）：CFF 没有 Encoding 操作符时就没有自己的码表，code 只能按标准编码解释
（code 指向一个标准字符串，charset 说明哪个字形带着这个名字）。之前只查程序自己的码表，而那张表在这种
字体里是空的，于是 code 找不到字形。

pdf.js `issue11279.pdf` 的符号 CMR10 子集只有 "one"、"A"、"B" 三个字形、没有 Encoding 操作符，页码
（"one" 的标准编码码位）就画不出来。修复后该文件与 PDFium 一致（0.01%）。同一处改动还带���
`issue6721_reduced` 7.82% → 2.65%、`bug860632` 2.94% → 1.20%、`issue3566` 3.85% → 3.00%。

**MMR 与 PDF 的 K 分开**：JBIG2 的 collective bitmap 是 Group 4（MMR），它此前借用 PDF 的 K=-1，
而 PDF 的 K=-1 指的是「无 EOL 的 Group 3 1-D」。两个含义混在一起，改动一个就伤到另一个。现在 MMR
有自己的入口（`DecodeMmr`），两者不再共用一个数值。

**本轮试过但放弃的两条**（记在这里，免得再走一遍）：

- 内联图像的 `/DP<<...>>` 不被解析（`ReadInlineValue` 只处理数组），所以内联 CCITT 的 `/K -1`、
  `/Columns` 都丢了。补上之后 `issue14999` 变好（17.81% → 10.07%），但 `issue4706` 变差
  （5.18% → 10.39%），而 `issue4706` 的内联 CCITT 并不是它差异的来源——**回退**，只保留 MMR 的分离。
  （第四十轮：退化来自回退位置算错而不是解析本身；同样的改动现已落地，两个文件一起变好。）
- 字体描述符的 `/CharSet` 给出字形名列表，可以把 PDF 侧的私有名（"Psi"、"Gamma"）换成程序侧的名字
  （"y"、"T"）。TeX 字体正是这样命名的（`issue6132`、`issue14462_reduced` 等），但 CFF 的 charset
  与 CharStrings 的顺序并不一致，要正确路由需要完整的 Type 1C 语义，**改期**。

**剩余的墨迹缺失**（19 个文件，仍在清单里）大致分三类：TeX 私有编码字体（`issue6132`、`issue14462_reduced`、
`issue4934`、`issue4573`、`issue8586` 等，需要完整 Type 1C 语义）、Type 3 字形空间的病态布局
（`issue14999_reduced`）、以及注释外观（`rc_annotation`、`bug1538111`、
`annotation-highlight-without-appearance`）。


## 第三十七轮 —— 字形从轮廓填，不再走字形图集

Skia 可以从**字形图集**（glyph atlas）里取预栅格化的字形来画一串文字，也可以从字形**轮廓**填。
我们一直走图集那条路（注释里写的是「图集更快」）。图集里存的是每个字号下的覆盖率掩模，
它的抗锯齿把字形画得比解析路径填充**略淡、略窄**——`endchar.pdf` 的 "LE HOLD-UP PLAN"
整个窄了一个像素，笔画也错开。

改成从轮廓填（`2e4d895`）之后：**139 个语料文件向 PDFium 靠拢，只有 1 个变远**；Skia 落在
1% 以内的文件从 532 涨到 549，5% 以内从 254 涨到 263。而且**并不慢**：整份语料用图集渲染 3m40s、
用轮廓渲染 3m42s——在这个尺寸上图集没带来任何好处。

顺带把字体的 hinting 关掉（`c8c5eb1`）：PDF 规范不让字形吸附到像素网格（ISO 32000-1 9.7.3.3），
而我们这个「1 单位」的字体给出的轮廓正是栅格化器 hint 之后的形状——hint 是在一个谁也看不到的
尺寸上做的。

**试过但放弃的一条**：把细于一个设备像素的描边加粗到 1 像素。`bug1743245.pdf` 的网格线因此
从灰变黑、差异 11.48% → 2.78%；但 `vertical.pdf` 的 0.1pt 横线在 PDFium 那里是**灰**的
（覆盖率 47%），加粗后变成实黑，而那个测试正按「要么图集、要么路径」两种墨迹量设了带。PDFium 对
亚像素宽度的线是按实际覆盖画的，不是统一提到 1 像素——**回退**。

## 第三十八轮 —— CFF 容器带上自己程序的字符表

Poso 围绕裸 CFF 程序搭的 OpenType 容器里的 **cmap 是空的**，后端打开这个字体后没法把字符映射到
字形，只能按自己的顺序给字形编号——而这个编号和程序里的顺序并不总是一致。pdf.js `issue6132.pdf`
的 cmbx12 因此每个字母都取了错误的轮廓，整页画成等宽的乱码。

现在容器带一个从程序自己的 charset 经 Adobe Glyph List 生成的 (3,1) format 4 cmap，核心也同步改
走后端：按字形名寻址的字体先解析成字符、再向后端要这个字符的字形，拿回的 id 一定取到正确的轮廓
（`1d9dc20`）。六个语料文件向 PDFium 靠拢，没有一个变远：`issue6901` 6.93% → 4.32%、
`issue7439` 3.85% → 1.82%、`issue14462_reduced` 8.02% → 7.02%、`issue11477_reduced` 4.23% → 3.48%、
`bug859204` 3.20% → 2.90%、`issue6132` 7.23% → 7.20%。

## 第三十九轮 —— Huffman 符号字典里零实例的条目保留自己的槽位

最后一个「Poso 全白、PDFium 有墨迹」的图片文件。三个 `bitmap-symbol-symhuffrefine*` 测试向量的段
结构一致（页面信息、普通 Huffman 字典、SDREFAGG 字典、文本区域），只有 `textrefine` 在文本区域上
多了 SBREFINE。第三十二轮修好的是 `one` 和 `several` 的细化与聚合；`textrefine` 仍然退化成 null、
整页画空。

这次逐字对照的是**当前版** PDFium（pypdfium2 5.14 带的是 2023 年重构后的布局；本地 2019 年的
PDFium 检出恰好在下面这几个点上过时）。四处差异，全部属实：

- **REFAGGNINST == 0**：PDFium（`jbig2_sdd_proc.cpp`）在分支判断**之前**就 `BS = nullptr`，所以零
  实例的条目存成一个没有位图的符号，保留槽位、照常导出、放置时跳过。Poso 之前对整个字典返回
  null，直接废掉整页——这就是空白页的直接原因（`textrefine` 的字典里有两个这种条目）。现在空
  符号以 `Jbig2Symbol.Empty` 走完全程。
- **聚合的角点**：多实例聚合是嵌套文本区域，PDFium 固定 `REFCORNER = JBIG2_CORNER_TOPLEFT`。
  Poso 之前传 0——但 PDFium/jbig2dec 把 BOTTOMLEFT 编号为 0，TOPLEFT 是 1——于是聚合里的字形被
  整体上移自身高度、掉出画布。解码器现在用与它一致的角点枚举命名。
- **聚合的符号 id 位宽**：PDFium 给聚合实例编号用
  `max(1, CeilLog2(SDNUMINSYMS + SDNUMNEWSYMS))` 位——宽度按字典**声明**的符号总数，而不是当前
  可用的个数。对 `textrefine` 这是 4 位，而 Poso 读 3 位，后面每个值（RDW/RDH/RDX/RDY/BMSIZE）
  都从错误的位开始解；跟踪里看到 157 字节的段解出 BMSIZE=5145。`several` 只是凑巧躲过（共 6 个
  符号，两个公式都给 3 位）。
- **空引用的失败语义**：PDFium 会在「细化一个空符号」时判字典失败、在「文本区域实例细化空符号」
  时判区域失败，Poso 现在同样失败；算术文本区域对齐 PDFium 更严格的契约（id 越界或实例为空就
  中止整个区域），Huffman 路径则跳过空实例且不计入实例数——PDFium 的 `continue` 仍会读条带的下
  一个坐标增量，移植版保持了这个流程。

`textrefine` 现在与 PDFium **逐像素一致（7.24% → 0.00%）**，`several` 也归零（0.29% → 0.00%）：
PDFium 对三个向量渲染成同一张图，我们现在也是。同方法全语料 A/B（out24 → out23）零回归。「Poso
全白、PDFium 有墨迹」降到各后端 **1 个**（`rc_annotation`，注释 chrome，按计划只记录不修）。
测试：语料文件本身加进既有的 Huffman 字典 Theory（`jbig2-huffman-textrefine.pdf`）。

## 第四十轮 —— 内联图像字典认得字典值，也认得关键字值

内联图像键值有两个事实（ISO 32000-1 8.9.7 表 89）：键后面的值可以是**关键字**（`/IM true`、
`/I false`），也可以是**字典**（`/DP<</K -1 /Columns 106>>`）。`ReadInlineValue` 两者都不认：关键字
值直接落到 null，`<<` 也不是合法值的起始。于是 pdf.js `issue14999_reduced.pdf` —— 一个 Type 3 字体、
字形描述是内联 1-bit CCITT 图像遮罩 —— 同时丢了遮罩标志和参数：掩膜按默认的 Group 3 二维模型解码
（码流其实是一维的），又因为没有 `/IM true` 被当成不透明灰度场来画，而不是按文字色做模板刷。整页在
真实文字下面只剩一层淡噪声。

只修字典不修关键字时文件反而**变差**（10.07% → 17.81%）：解码正确的掩膜是「填充场 + 字母形状为
洞」，缺了掩膜标志时画出来的恰恰就是这个洞外区域。两处必须一起修。第三十六轮「放弃」笔记里
issue4706 的退化也解释清楚了：第一次尝试用 `Position - 1` 回退，对 `[` 是对的，但 `<<` 是两字节，
回退后落在**第二个 `<`** 上，字典解析成垃圾。改成回退到 token 自己的起点后两个文件同时变好。

移动的文件：`issue14999_reduced` 10.07 → 6.50（字母完整、位置与 PDFium 对齐，残差是笔画粗细）、
`issue4706` 5.26 → 2.91、`bug1245391_reduced` 5.18 → 3.97、`images_1bit_grayscale` 9.40 → 8.79、
`issue6605` 0.85 → 0.72、`bug1795263` 0.47 → 0.39，另有两处小幅。全语料零回归。测试：语料文件以
`inline-imagemask-ccitt.pdf` 入库，任一半回退都会失败（噪声 1.2%、不透明场约 13%、修复后 8.3%）。

## 第四十一轮 —— loca 表乱序的字体子集，以及没有字符表的 CID 字体

**语料里有 84 个文件嵌入了没有 cmap 的 TrueType 程序**（扫描脚本已随工具入库）。CID 字体的码
本身就是字形号，所以缺字符表是合法的——但后端若要靠猜「字符→字形」的映射就会猜错：Skia 的
`GetGlyphPath` 对这类字体的大多数字形号返回**空路径**，于是那些字母干脆不出现。这批文件的中位
差异是其余语料的 3.8 倍（1.17% 对 0.31%），83 个可比文件里有 26 个超过 3%。

最清楚的一个是 pdf.js `issue11131_reduced.pdf`：Identity-H 的 CIDFontType2，71 个字形的程序没有
cmap，其中**八个字母消失了**（CID 4、8、9、13、23、24、28、48——正是 Skia 够不到的那批 id）。
PDFium 把这一行的 23 个字母全画了出来。

**两个独立的毛病，都已修。**

*`loca` 表可能是乱序的。* 这个子集的 `loca` 不单调（`loca[4] = 10674`、`loca[5] = 2188`），
69 个互不相同的边界值恰好平铺了 `glyf` 表，但 71 个字形里有 37 个的区间是**倒着**的。读取器若
拿区间长度当字形大小，就算出负长度，那里自然什么也没有。**长度只是上界：记录自己带着自己的
大小**。FreeType 明明白白这么写（ttpload.c）：`if (pos2 >= pos1) *asize = pos2 - pos1; else
*asize = glyf_len - pos1;`——字形保留，长度放宽到表尾。Poso 现在照做，那八个字母就回来了。
而**空**字形（`loca[g] == loca[g+1]`）是另一回事，必须保持为空：它的偏移指向后面那个字形的
数据，在那里读会画出下一个字形。

*后端够不到它映射不出来的字形。* CID 字体的码就是字形号，根本不经字符查找——但 Skia 的
`GetGlyphPath` 走的是它自己的「字符→字形」那条路，对一个它映射不出来的 face，大多数 id 都
返回空。所以没有 cmap 的 CID 字体改为**按字形**从程序自身的轮廓取：后端够得到的那些仍归后端
（它画得更好），够不到的才从程序取。整字体都改走程序反而更差——`issue4402_reduced` 掉了 2 个
百分点，那里的字母后端画得很好。

移动的文件：`issue11131_reduced` 5.32% → 3.85%，`bug1650302_reduced` 3.59% → 3.10%，全语料
零回归。前者的残差是笔画粗细。测试：语料文件以 `cidfont-nocmap-loca.pdf` 入库，没有 loca 修复
时会失败（整页只有 1.9% 墨迹，修好后 4.6%）。

**但字形当时还是多边形**。字形找到之后画出来是直杆方肩，而 TrueType 的轮廓不是直线拼的：
off-curve 点是「到下一个 on-curve 点为止的那条二次曲线」的控制点。读取器把这个控制点当成了
三次曲线的两个控制点都传出去，曲线就被压成了一条直线。现在每条二次曲线都升阶成描同一条曲线的
三次曲线——控制点落在两端到二次控制点的三分之二处，和 Skia 后端对自己拿到的轮廓做的转换
一样。

走点时还要数清一条曲线**消耗**了几个点：两个连续 off-curve 点之间有一个隐含的 on-curve 点，
而**第二个** off-curve 点是「从那个隐含点出发」那条曲线的控制点，所以这一对要当成两条曲线
走、消耗两个点；原来的计数把第二个点用了两次。

现在字形是对的——肩部收窄、笔画带弧。页面的数字相对 PDFium 动了 0.15 个百分点
（3.85% → 4.00%），那是亚像素光栅化的落点与折线版不同，而不是形状仍然不对：字形的位置、
尺寸、墨量全都对得上，而且 PDFium 自己画这一行用的也是曲线。

**这次诊断的代价**。最初对这个字体的读法——`loca` 乱序、记录长度永远对不上区间、记录链走两步
就断、fontTools 拒绝这个文件——全都指向「字体是坏的」。它不是：FreeType 能从它读出全部 71 个
字形，Acrobat 也能。定案的证据不是继续分析字节，而是**问一个真正的 TrueType 实现**——这才是
值得记住的教训：一个参考实现一分钟给出的答案，反向推导一小时也没给出来。

**顺带把另外两条轮廓路径也查了一遍**（前面四轮改动都落在 `TrueTypeOutlines` 上，所以 CFF 和
Type 1 那条路用同样的方式复查了）。Poso 有三个轮廓读取器：`TrueTypeOutlines`（二次升三次）、
`CffOutlines`，以及 Type 1 那条——它先转成 CFF 再走 `CffOutlines`。后两者都在**原生三次**
贝塞尔上工作（Type 2 charstring 的 `curveto`/`flex` 本就是三次），所以都不可能有「把曲线画成
直线」这类降级。这一条是读代码确认的，不是再跑一次渲染。

复查还把我们的读取器和 FreeType 做了全量交叉比对：**586 个内嵌程序、32517 个字形**，逐字形比较
em 空间里的包围盒（脚本在 `_scratch/corpus_cmp/crosscheck_outlines.py`，配一个小的 FreeType
导出器）。**95.2% 完全一致**。其余分三类，都不是绘制错误：

- **729 个字形相差不超过 0.005 em。** 二次升三次作为曲线是精确的，但控制点位置变了，而我们的
  包围盒是按控制点算的——于是数字动一点，形状不动。
- **513 个字形是「同尺寸、整体平移」。** FreeType 通过 phantom point 把 `hmtx` 的左偏移算进度量
  （`ttgload.c`：`pp1 = xmin - lsb`），我们报的是字形记录自己的范围。已确认不影响渲染：由这类
  字形组成的页面，墨迹质心与 PDFium 相差不到一个像素。
- **327 个字形是别的情况**，多数是**只有一个点**的轮廓被我们丢掉（`count < 2`）而 FreeType 保留。
  单点轮廓没有面积，画不出东西，只体现在包围盒上。最清楚的例子是某个注释字体的 `.notdef`：它的
  第五个轮廓就是下伸部上那一个点，差异全部来自它。

所以「曲线变直线」那一处是这一类里唯一的真缺陷，剩下的数字是度量约定的差别，而不是形状还不对。

## 第四十二轮 —— TeX 字体的内置编码表，以及它的 charstring 画出的曲线

pdf.js issue6132.pdf 是一篇 TeX 排版的论文，标题用 cmbx12 排——一个私有编码的 Type1C 程序：PDF 的
/Encoding 只声明了六个字形名（Gamma、Psi、Omega、fl、dieresis 等），其余的码全部要走程序**自带的
CFF 内置编码表**。两个码在那里断了——14（"Efficient" 里的 ffi 合字）和 12（"Application" 里的 fi）
——而且 E、F、t、a、d、e、o、p、s 这些字母整页缺失或变形。CFF 这条路上有三个互相独立的缺陷。

*内置编码表解析错了。* CFF 规范 2.24 的 format 0 按字形顺序每字形列一个**码**；format 1 列的是
(首码, nLeft) 的区间，码和字形号都跨区间连续递增——表里根本没有「字形号」字节。旧读取器把
format 0 的每区间只前进一个字节（区间确实是一字节，但字形计数器也要每区间加一，旧代码差了一），
把 format 1 读成 (码, 字形) 字节对——这种布局根本不存在。补充表（format 字节 & 0x80）用 **SID**
指称字形，SID 只有经过 charset 反查才能落到字形号——旧读取器手里没有 charset 可查。修复后按规范
解析两种 format、按 charset 反查补充表，边角情况逐条对齐 FreeType 的 `cff_encoding_load`（PDFium
正是通过它自带的 FreeType 走这条路）：字形 0 永不被编码、码数超出字形总数的丢弃、offset 0/1
（预定义 Standard/Expert 编码）留给调用方的回退路径。

*Skia 自带的 CFF 解析器对这个程序的一部分 charstring 返回空路径。* 编码修好后合字出来了，但整页
墨迹只有 3.2%，PDFium 是 7.8%。探针显示 Skia 的 `GetGlyphPath` 恰好在缺失的那些字母上失败（字形号
50、68、70、71、98、101、102、112、113、116、117），而 FreeType 和 PeachDrawing 从同一个包装容器
里把每一个都读了出来。第四十一轮的无 cmap CID 路线已经有正确的形状：逐字形地问后端，后端够不着的
才从程序自己的读取器取。现在简单字体的 CFF 程序也走这条逐字形探测（探测的是后端自己的字形号，
因为后端就是拿它画画的）。

*我们的 charstring 解释器把 hvcurveto/vhcurveto 的增量放到了错误的轴上。* 回退把字母画出来了，但
E 的中间横臂缺一个楔、C 变形。`hvcurveto` 的每组四个操作数是 (dx1, dx2, dy2, dy3)——曲线从水平轴
出发，所以*第三个*操作数属于它不出发的那个轴；最后一组的第五个操作数给那个轴补上剩余坐标
（是 dx4，不是 dy4）。旧读取器把 args[3] 给了 dx3、args[4] 给了 dy3，把交替曲线每一组的两个轴
份额对调了。FreeType 的 `cffdecode.c` 对此写得明明白白。修复后，**cmbx12 全部 130 个字形的
在线点与 FreeType 逐点一致**（唯一差别是每轮廓一个显式闭合点，填充结果相同）。

**A/B 抓出的一处回归。** 逐字形探测扩展到简单字体后，Peach 在 `glyph_accent` 和 `issue7101` 的
seac 组合字形上丢了墨：Peach 的 `TryGetGlyphOutline` 对 seac 字形回答「没有轮廓」，而它的文字
路径在绘制时会自己展开 seac。探测和绘制必须回答同一个问题——这张脸能不能画这个字形——于是探测
现在和绘制一样展开「基字+重音」对。两个文件都回到基线数字，`issue8480`（同为 seac 组合）在
Peach 上 6.68% → 3.30% 是顺带的收益。

各文件变化（未注明即 Skia）：issue12399_reduced 8.39% → 3.60%，bug1308536 10.45% → 8.02%，
issue6132 7.20% → 6.22%（墨迹 3.22% → 6.74%，PDFium 为 7.79%），issue3566 2.19% → 1.60%
（Peach 1.93% → 1.34%），issue8480 Peach 6.68% → 3.30%。全语料 977 个文件零回归
（report27 → report28）。测试：issue6132 的标题条作为 `tex-private-encoding.pdf`
（修复前 Skia 在条上墨 5.1%，修复后 10.2%；PDFium 11.2%）。

这一轮的取证照旧守规矩——不靠猜字节。编码表的真实布局是把 FreeType 的加载器和 fontTools 的
解码器并排读出来的；轴对调是把 cmbx12 那个 "E" 的操作数逐个与 FreeType 的轨迹对出来的；Skia
的失明是在一个 FreeType 加载毫无怨言的容器里逐字形探 `GetGlyphPath` 探出来的。

## 第四十三轮 —— 没有 ND 标记的 Type 1 charstring 条目不会吞掉下一个字形

pdf.js issue14462_reduced.pdf 只有一行字——"Issue 14462"——来自一个原生二进制 Type 1 程序
（`FontFile`，带 eexec 的 PFB）。Poso 只画出一个字符：整个标题除了最后一个数字全部消失，而且两个
后端表现一致，说明毛病在程序读取器里，不在任何一个光栅器里。

这个 PFB 的 CharStrings 字典每条都写成 `/名字 长度 RD <二进制> ND`，但生成器给很多条目省掉了
`ND`，于是 charstring 数据后面紧跟的字节就是下一条的 `/`。读取器处理了这种情况——它会把流后退
一格跨过读错的那个 token——但后退少走了一步：光标移动了，却没收下它落到的那个字节，tokenizer
的前瞻字符比光标超前一个字节。下一次 `GetToken` 于是把 `/` 分隔符当成了字形名，每个真字形后面
都跟了一个假条目（`/` 或二进制乱码，零长度 charstring），假条目的空 charstring 又把真条目挤出了
码→字形表。把程序解密并 dump 解出的 CharStrings 后，损坏直接可见：`zero`、`three`、`F`、`r`……
与 `/` 条目和乱码名交替出现，而编码本该是单调的 `zero one two three ...`。

修复让后退动作连字节一起消费，与 pdf.js 的 `prevChar`（`stream.skip(-2)` 之后再 `getChar()`，
它的注释里点的正是这个文件）一致：光标停到读错 token 起点再往前一字节，tokenizer 的下一次读取就
又能看到那个 `/`。84 个字形名现在按程序顺序全部解出，CFF 转换把它们带过去，整行完整渲染。

各文件变化：issue14462_reduced 7.02% → 2.44%（Peach 7.04% → 2.11%；墨迹 0.62% → 5.97%/6.10%，
PDFium 为 6.64%）。全语料 977 个文件零回归（report28 → report29）。测试：语料文件作为
`issue14462-reduced.pdf`，修复前全页墨迹 0.6%，修复后 6.0%。

这一轮的教训在失败模式本身：一个对「token 在哪结束」*几乎*正确的解析器不会在字体上明显失败——
它会悄悄伪造条目，而渲染证据（上百个缺失字母）又把矛头指向光栅器而不是字节光标。

## 第四十四轮 —— 编解码器看不到的 /Decode 数组、掩码下的渐变，以及用图案填充的文字

绘制路径上的四个缺陷，每一个都是跟着「参照引擎有墨、我们没有」的地方查出来的。

*DCT 路径忽略了 /Decode。* JPEG 的样本要按图像的 /Decode 数组重映射——`min + 样本 * (max -
min)`，也就是 PDFium 在 `CPDF_DIB::GetDecodeAndMaskArray` 里算出来、再施加到解码器返回结果上的
那对 `decode_min_`/`decode_step_`。编解码器看不到字典，而我们又把解码后的位图直接交给了页面，
于是用 `/Decode [1 0]`（取补形式）画的文件出来的是存储色调而不是反相后的：pdf.js issue7406.pdf
的 logo 印成白底黑字，而所有查看器都是黑底白字。现在 DCT 路径无论编解码器给出几个通道，都会
把数组施加到解码结果上（CMYK 那条路本来就有自己的映射）。唯一的周折：issue7406 的 JPEG 是
progressive（SOF2），我们的管理解码器拒解，于是位图来自后端、映射跑在它的 BGRA 上。

*alpha 读取器把 JPEG 当原始样本读。* 同一个文件的 Luminosity 软掩码也用 JPEG 存覆盖率，而
`DecodeAlphaChannel` 把流的字节当成一行行像素——于是掩码是噪声，被它掩的元素直接消失。软掩码
和其他灰度图像一样，生成器完全可能用 JPEG 存它，所以现在也走解码器，取单通道的那个平面。

*渐变看不到 ExtGState 的软掩码。* `sh` 操作符把渐变直接画到页面上。其他所有绘制都会先打开掩码
的组，渐变是唯一不打开的例外，于是 pdf.js issue18956.pdf 的渐隐渐变回来是一条实色带。`sh` 现在
和其他绘制一样打开组。

*文字不能用图案填充。* `scn` 选中的 shading 或平铺图案是「颜料」而不是「颜色」，没法当颜色交给
文字绘制。现在把这个 run 的字形画到一张白底表面上，该表面成为图层掩码，图案在图层内跨整个设备
绘制——于是恰好覆盖字形。pdf.js issue9243.pdf 就这样用 shading 图案画标题，词出来是纯黑。两条
字形路径都参与：后端能画的那些，以及后端画不了、由内嵌程序自己的轮廓补的那些。

图案填文字这条路需要自己的深度守卫：平铺图案的格子可以包含文字，而那文字又可以再用图案填充，
而格子是由一个**全新的**解释器渲染的、计数器从零开始。语料里正有这样的文件
（ContentStreamCycleType3insideType3），如果不把深度带进格子解释器，渲染就会递归到栈溢出。现在
深度会传进嵌套解释器，超过四层就丢弃图案绘制，让页面其余部分照常。

*径向渐变选错了根。* Type 3 渐变的参数是两个圆所定义二次方程的根，哪一个是可见的取决于锥怎么
走。我们偏向小根、半径为负时退回另一个根，于是相交圆的配置被填成一整块实色。PDFium 的
`DrawRadialShading` 按锥的方向判断——半径收缩且两圆互相包含时，扫描是反向走过的——而且二次项
中间那一项带因子 2，我们的求解器漏了。求解器现在逐字照它写：pdf.js radial_gradients.pdf 的四种
圆配置（13.81% → 0.49%）一圈一圈都对上。

各文件变化：issue7406 15.93% → 7.16%（Peach 15.48% → 7.63%），issue18956 13.57% → 6.14%，
issue9243 13.79% → 1.14%（Peach 0.03%），radial_gradients 13.81% → 0.49%。图案填文字的这条路还
带好了别的文件：pattern_text_embedded_font 10.10% → 3.28%，ZapfDingbats（Peach）9.03% → 7.31%，
ShowText-ShadingPattern 1.76% → 1.08%，issue19360 1.05% → 0.48%，issue8111（Peach）
0.55% → 0.00%。全语料 977 个文件零回归（report29 → report30）。测试：四个语料文件各作夹具，各自断言被破坏的那个性质——取补色调、
渐隐色带、词上的渐变、四个面板的墨迹总量。

*编码 CMap 也可以用 bfchar 携带映射。* bfchar/bfrange 属于 ToUnicode CMap，但把两者写反的
生成器并不罕见，读取器也都接受：pdf.js 把目的字符串的字节读成 CID（`convertCidString`——一字节
是值本身，两字节是 `(b0 << 8) | b1`）。pdf.js bug920426.pdf 的 /Encoding 就是整张映射都由 100 条
bfchar 组成的 CMap，它的文字现在渲染成 "Checkliste Service"。PDFium 在那里忽略 bfchar、画 .notdef
方框，所以这一条记入 `oracle_wrong.tsv`；与 PDFium 的语料差异只从 16.60% 挪到 14.10%，因为参照
引擎自己站错了边。

**两个不是 Poso 的问题，经仲裁确认。** `function_based_shading_cmyk` 看着像颜色 bug（与 PDFium
差 22.75%），但 MuPDF 与 Poso 只差 0.81%、与 PDFium 差 19.29%：PDFium 把 2x2 采样 CMYK 函数的远角
卷绕了（样本说是黄+黑的地方它画成深蓝）。记入 `oracle_wrong.tsv`。`issue16038` 的 hatch 线差异
是因为笔画只有 0.3985pt——2 倍下 0.8 像素：PDFium 把它吸附到一行像素上，我们是跨两行抗锯齿，而
墨迹统计把偏淡的第二行也计入。这正是本项目不追的「亚像素细线」类。

**关于 Peach 逐次运行的噪声。** 整份重渲会改变四个 Peach 数字，而任何代码改动都解释不了
（ZapfDingbats 7.31% → 9.03%，issue8092 0.50% → 0.93%，issue15443 与 issue7580 约 0.1pp）。这四个
文件都没有内嵌 CMap，而且用同一个二进制单独渲染 ZapfDingbats 又回到 7.31%——PeachDrawing 对某些
文件的输出取决于同一进程里此前渲染过哪些字体。断点续跑与一次性跑之间的这种量级的 Peach「回归」
就是这类噪声，不是代码改动；Skia 不共享这类状态，是仲裁者。

**关于在测试里量墨迹的一点说明。** 径向渐变的修复在单元测试里一度像失败，而语料渲染显示它是对的：
测试只数一个颜色通道低于阈值，而这条渐变是红和绿的。数墨迹要数最暗的通道，这正是语料比较的亮度
口径做的事。

## Poso 的真实问题，按严重程度排列

1. **`issue19634`（20.95% → 6.74%）：Type3 字形的两处变换错误，都已修**（`4f9d00b`、`683abb5`）。
   第一处是字形过程应按「隐式 q/Q」执行（ISO 32000-1 9.5.5），而 `ExecuteType3Glyph` 只保存了画布
   矩阵，没保存解释器自己的图形状态：F4 的 charproc 用 `gs` 设了 Luminosity `/SMask`，掩码泄漏到同
   位置后画的 F5 绿色文字上，整行被白色层遮掉（20.95% → 19.20%）。
   **第二处（第三十五轮）推翻了第二十七轮的仲裁结论**：字形过程应在**字体空间**里执行（画布 =
   FontMatrix × 文本空间 × CTM），但只有画布跟着变换，解释器的图形状态仍是页面的矩阵——于是 charproc
   自己的 `cm` 量错了基准。F4 的字形（d1 模板、按文字色填充的矩形）因此被画成**错位的大方块**拖在
   每行文字后面，而不是 PDFium 那种灰色柔和的阴影。修复后 19.20% → **6.74%**，墨迹从 4453 像素降到
   3669（PDFium 3841），阴影形状也对上了。
   此前把这 19.2% 的残余判为「PDFium 离群」（依据 MuPDF 92% 站在 Poso 一边）——那个仲裁本身没错，
   但它掩盖了真正的原因：Poso 的阴影本来就画错了。

2. **`pr12564`（Skia 10.03% / Peach 16.48%）：PeachDrawing 在极端缩放下布局错乱。**
   页面用 `12700 w` 配 `0.0000787402 cm` 画 1pt 细线，Peach 把整页文字画成散落的乱码，而 Skia 正常。
   这是**确定性**的（同进程渲染两次逐字节一致），不是 `SharedFontSet` 的跨文件污染；A/B 也证明它早于
   字体兜底改动就存在。根因在第三方 PeachDrawing 内部（无源码），Poso 侧修不动，只能记录。

3. **`smask_alpha_oob_transfer` —— 已修**（97.22% → 0.00%，见开头第一条）。此条从榜首移除。

**第二十五轮的分诊结论**：兜底机制上线后，把剩余 10%+ 的「真实差异」候选逐个查了一遍，除上面两条外
都不是 Poso 的语义 bug：

**第二十七轮的 MuPDF 仲裁**：对剩余 5 个未标注的 20%+ 文件（`issue16316`、`issue16038`、`issue14165`、
`copy_paste_ligatures`、`function_based_shading_cmyk`）跑了三向对比。没有一个呈现「两个参考一致、Poso
离群」的形态——三个渲染器两两互差（如 `issue16316`：mu-vs-poso 33% 反而小于 mu-vs-pdfium 47%；
`issue14165` 三方两两都差 93%+，是各自字体替换不同）。全部落在抗锯齿/hinting/替代字体的分歧带里，
**没有留下干净的 Poso 语义 bug**。字体保真线到此收口。

- `22060_A1_01_Plans`（17.36%）：hatch 平铺是一张大 JPEG（`/R13`，2480×2630），2 倍下细线偏淡；
  8 倍收敛到 PDFium 水平（墨迹 0.167 vs 0.144），几何正确，属降采样质量。
- `issue12337`（15.38%）：密集文字页，墨迹 6.09% vs 5.27%，肉眼两图一致，是 PDFium 文字略粗的抗锯齿差。
- `issue2884_reduced`（17.13%）：单行 CJK（Identity-H CIDFontType2），三后端字形完全一致，只差笔画粗细。
- `issue3458`（17.25%）：墨迹 14.17% vs 14.14% 基本相等，是均匀的颜色/边缘差，非缺内容。

也就是说，**剩余的大差异已经不再是「Poso 少画了东西」，而是「画了但笔画粗细/颜色与 PDFium 略有不同」**——
字体保真这条线到此基本收敛。

## 其他较大但可能合理的差异

`jpx_smaskindata`（54.38%，见第二十九轮的参照分歧）、`issue16038`（38.30%，条纹相位与 PDFium 不同，墨迹 15.6% 对 16.8%）、`issue16316`（38.16%）、`knockout_inner_backdrop`（34.50%）、`calgray`（72.76%，PDFium 直接按 g×255 输出并跳过传递函数，pdf.js 的参考公式才是文件期望的结果）、`issue19176`（62.57%，PDFium 忽略 `/UserUnit`，正确的一方是 Poso）。
完整排序清单见 `out/report5_triage.txt`。

## Poso 完全渲染不出页面的文件（Skia 10 个 / Peach 10 个）

两个后端的无页面集合现在已经一致。剩下 10 个里 **9 个 PDFium 同样拒绝**，唯一的例外是
`issue19517`：

- `issue19517` —— `Unable to allocate pixels for the bitmap`：MediaBox 12608×16806，2 倍下要
  25216×33612（约 3.4 GB）的位图，SkiaSharp 的分配失败，而 PDFium 恰好分配成功。这是调用方请求的
  画布尺寸超出可分配内存，不是渲染语义问题；降低 scale 即可渲染。
- `issue19484_1`、`issue19484_2`、`issue3371`、`pr6531_1`、`print_protection` —— 加密文件，
  PDFium 报密码错误或加载失败（`/Pages` 不可达）。
- `poppler-85140` —— PDFium 自己也 `MemoryError`。
- `poppler-937`、`REDHAT-1531897`、`poppler-395-0-fuzzed` —— PDFium 以页面或数据格式错误拒绝。

## 复现方式

流水线已入库到 `tools/corpus`（`harness.py`、`cmp_pdfium.py`、`sweep` 项目）。需要带
`pypdfium2` + Pillow 的 Python，以及一个 PDF 语料目录（`POSO_CORPUS` 环境变量，默认
`D:\_git0\pdfjs\test\pdfs`）。

```bash
cd tools/corpus
dotnet build sweep -c Release   # Poso 渲染驱动器
python harness.py manifest     # 生成 977 行清单
python harness.py pdfium       # 渲染参照引擎（可断点续跑）
python harness.py sweep        # 渲染 Poso/Skia 与 Poso/Peach，带崩溃二分
python cmp_pdfium.py           # 生成 report_pdfium.tsv + summary_pdfium.txt + triage_pdfium.txt
```

要在不覆盖基线的前提下衡量一次改动，把结果渲染到另一个目录，再让比较脚本指向它：

```bash
sweep/bin/Release/net10.0/sweep.exe manifest.tsv <other-dir> 2.0
POSO_DIR=<other-dir> REPORT_PREFIX=report_try python cmp_pdfium.py
```
