# LiteReader · Avalonia 跨平台版 —— 开发文档

> **本文档不是项目介绍。**「这是什么、能做什么、怎么跑起来」在 [README.md](README.md)；
> 这里只讲**怎么做出来的**：开发自检体系、工程结构与关键设计决定、
> 与 `LiteReader.cpp` 的逐项对照、以及待办路线图。
>
> ★ 章节编号**刻意沿用原 README 的 5 / 6 / 7 / 9**（第 1–4、8 章已移入 README.md）。
> 这么做的原因是本文档内部有大量 `§5.3 ⑤`、`§7.5 ①` 这样的交叉引用 ——
> 重新编号要全局替换一遍，而替换是**有漏改风险**的净收益为负的操作。
>
> 文中出现的 `spikes/`、`Diagnostics/`、`selftest/` 都是开发期工具与留痕，
> 不参与发布产物。

---

## 5. 开发自检（三个诊断模式）

Avalonia 的渲染问题通常是**静默**的 —— 不抛异常，只是画错或画不出来。
"中文有没有渲染成方块""暗色主题下控件是不是还是白底""行首缩进是不是被吃掉了"这类问题，
看代码看不出来，必须看图、或者用直接驱动内核的断言兜住。

| 命令 | 作用 | 需要窗口 |
|---|---|---|
| `LiteReader --fuzz [步数] [--fuzz-seed N]` | 编辑内核**差分对拍**（增量行索引 / 撤销重做 / **跨行着色状态**） | ❌ 不需要 |
| `LiteReader --bench-edit` | 大文件编辑性能基准 | ❌ 不需要 |
| `LiteReader --selftest <文件> [--selftest-out <目录>]` | 5 套主题截图 + **纯逻辑断言** + 编辑内核冒烟 + **能力在真实控件上的闭环**（彩虹括号 / 查找 / 补全 / 跳转定义 / 双击分词高亮 / 导航条 / 右键菜单）+ **工具层**（Ctrl+滚轮 / 窗口外框接线 / 外壳跟随主题 / 单实例 / 终端 / 文件关联）+ **非客户区**（外框配色 / 图标资源） | ✅ 需要 |

**三者都用退出码表达结果（全通过 0，有失败 1）**，可以直接挂到 CI 上做三平台冒烟。
三者按固定顺序串成一条验收链的做法见 **§5.5**（输出留痕在 `selftest/verify.log`）。

> ⚠️ **在 Windows 上请用 `dotnet LiteReader.dll …` 而不是直接跑 `LiteReader.exe` 来收集输出。**
> exe 是 GUI 子系统（`WinExe`），在 PowerShell 里 `*>` 重定向拿不到任何 stdout，
> 表现是「日志文件是空的、而退出码还是 0」—— 很容易误判成「跑过了」。
> 走 `dotnet <dll>` 是控制台宿主，重定向可靠（顺带记得 `[Console]::OutputEncoding = UTF8`，
> 否则中文全变乱码）。

> `--fuzz` / `--bench-edit` 走的是 `Program.Main` 里的提前返回分支，
> **不创建窗口、不需要显示环境** —— 这一点在 CI 上很关键，
> Linux 构建容器通常没有 X11/Wayland。

### 5.1 差分模糊对拍（`--fuzz`）

`DocumentModel` 为了大文件流畅，把「每次编辑重扫全文行索引」换成了
「只重切受影响区间 + 尾段整体平移」。这个优化一旦有边界漏判
（在行首/行尾/文件末尾/CRLF 中间编辑、跨多行删除、粘贴带换行…），
表现是「行号错位、渲染串行」这类很难定位的问题。

所以用一个**与被测实现完全独立**的朴素实现（`RefSplit`：全文重新按 `\n` 切分）做对拍：
随机编辑一步 → 比对文本、行数、每行内容、每行起始偏移、偏移↔行列往返，
以及**每行的起始着色状态**（见下）。

```
$ LiteReader --fuzz 20000 --fuzz-seed 7
[fuzz] 步数=20000  随机种子=7
[fuzz] 语料：7,429 字符 / 401 行（含 CRLF / LF / 中文 / 制表符）
[fuzz] 其中 57 行的起始状态非干净（跨行块注释 / 未闭合字符串 / 括号深度）
[fuzz] 增量行索引 + 跨行着色状态 × 20000 步：与全文重切/逐行全量推进完全一致 ✔
[fuzz] 撤销/重做 × 400 步往返：完全还原 ✔
[fuzz] 全部通过 ✔
```

实测 **6 个种子 × 20000 步 = 12 万步**全部一致
（seed 1 / 7 / 42 / 101 / 12345 / 20260918）。
这个对拍在开发过程中抓出了 **4 个真实的边界 bug**，全都是只差一行或一个 `\r`、
常规用例与肉眼都发现不了的那种（见 §5.4 末尾）。

#### 「跨行着色状态」这一维为什么必须进去

跨行状态（`LineState`：块注释 / 未闭合字符串 / **括号嵌套深度**）和行索引是**同一套**
「编辑后局部失效、按需重算」的机制 —— `DocumentModel.Replace` 里把
`_stateComputed` 退回 `firstLine + 1`，就是这套机制的开关。

它出问题时和行索引一样是静默的：**不抛异常、文本完全正确**，只是下游若干行的颜色不对
（彩虹括号整体错位、注释段被当成代码）。所以参照实现写成「从第 0 行起逐行推进着色器」的
朴素版本，**完全不碰懒推进缓存** —— 被验证的是缓存的失效与重算，而不是着色算法本身。

另加一条防呆：语料里必须**真的存在**非干净的起始状态（实测 57 / 401 行），
否则「比对起始状态」永远成立、这一维等于没测。

### 5.2 编辑性能基准（`--bench-edit`）

12.9 MB / 100,001 行的文档上：

```
[bench] 载入 12.9 MB（UTF-16，100,001 行）：建索引 18.3 ms
[bench] 单字符插入 ×400：中位 8.41 ms  p90 9.88 ms  最大 30.18 ms
[bench] 其中「整串重建文本」本身：中位 8.09 ms
[bench] 撤销 1 步：10.35 ms
```

**关键读法**：单次插入 8.41 ms 里有 8.09 ms 是「整串重建文本」的纯拷贝成本 ——
也就是说**行索引的增量更新只花了约 0.3 ms**，这条路已经优化见底。
再往下要换 piece table / 间隙缓冲才能省掉那 8 ms，那是另一个量级的改造。

### 5.3 视觉与内核冒烟（`--selftest`）

```bash
# 5 套主题各截一张 PNG + 纯逻辑断言 + 编辑内核冒烟 + 能力在真实控件上的闭环
dotnet LiteReader.dll --selftest <文件路径> --selftest-out <输出目录>
```

共 **305 条断言**（`✔` 条数，实测输出无 `✘`），分三层：

| 层 | 套件 | 条数 | 特点 |
|---|---|---|---|
| 纯逻辑 | 跨行状态机 13 / 彩虹括号 13 / 括号配对 12 / 查找 12 / 跳转定义 7 / 代码补全 10 / **双击分词 17** / **导航条几何 14** / **窗口外框配色 22** / 主题键完整性 10 | **130** | 快、稳，能塞进大量边界用例 |
| 真实控件 | **图标资源 5** + 编辑内核冒烟 14 + 能力闭环 64 | **83** | 要开窗口，但能测到「接线」 |
| 工具层 | Ctrl+滚轮 9 + **窗口外框接线 10** + **外壳跟随主题 26** + 单实例转发 14 + 终端清单 17 + 文件关联 16 | **92** | 有纯函数也有真实收发回路；见 §5.3 ⑥ |

> 计数方式：直接数日志里的 `✔` 行（本机为 `305 ✔ / 0 ✘`）。**不要凭印象写这个数字** ——
> 它随断言增删立刻漂移，写完顺手核对一次。
> 这个数字本轮就被纠过一次：README 原先写「四项能力闭环 37 条」，按 `✔` 行实际数出来是 **36**。
> 再往上追一次：`能力闭环冒烟（真实控件）` 是 **64**（原 36 + 双击分词 10 + 导航条 10 + 右键菜单 8）。
> **2026-09-22 又纠一次**：文档原写 304（工具层 91 / 文件关联 15），
> 按 7 份独立日志（5 种发布形态 + 重建后的托管产物 + 发布后的 AOT 产物）逐一数，
> 每份都是 **305**，且 `文件关联（纯函数）冒烟` 那一节实际是 **16** 条 → 工具层 **92**、总计 **305**。
> ★ **写这个数字时踩过一个坑**：`✔` 与 `✘` 一旦被 PowerShell 用错误的代码页解码，
> 在日志里**长得一模一样**（都是 `鉁?`），于是「失败 1 项」会被读成「通过」。
> 所以先 `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` 再重定向，
> 或者用 `spikes/winshot/parse_selftest.py` 解析（它按 UTF-8 读、按套件分节计数，
> 并且会跳过自带 `✘` 的汇总行）。
> 最省事的核对方式是按套件分别数（脚本见 §5.5）。

三层都要：纯逻辑层测不到「逻辑算对了但没人调用、候选算出来了但没画出来」；
真实控件层慢，塞不下几十条边界用例；工具层那几样（注册表、外部终端、跨进程管道）
**大多带副作用或依赖真实进程**，只能把可断言的部分（纯函数、清单结构、进程内收发回路）单独摘出来测。

**① 主题截图 + 配色断言**（**这是判断裁剪是否破坏主题字典的关键证据**）：

```
[selftest] One Dark Pro     → theme-One_Dark_Pro.png       背景=#ff282c34 关键字=#ffc678dd
[selftest] One Light        → theme-One_Light.png          背景=#fffafafa 关键字=#ffa626a4
[selftest] VS Code Dark     → theme-VS_Code_Dark.png       背景=#ff1e1e1e 关键字=#ff569cd6
[selftest] IntelliJ IDEA    → theme-IntelliJ_IDEA.png      背景=#ff2b2b2b 关键字=#ffcc7832
[selftest] 极致黑 AMOLED      → theme-极致黑_AMOLED.png       背景=Black 关键字=#ffc792ea
[selftest] 文档摘要：UTF-8   行 29   字符 680
```

`DescribeColor` 故意用一个不可能撞上的兜底色（Magenta）去查字典 —— **一旦出现 `#ffff00ff`，
就说明主题字典没加载到**（裁剪把它裁掉、或 `x:Class` 写错），此时截图也不可信。

**② 跨行状态机冒烟（13 条断言）** —— 块注释跨行、未闭合字符串跨行、
行注释里出现 `"` 与 `*/` 都不该生效、以及编辑后状态是否被正确失效重算。
全部是对 `SyntaxHighlighter` + `DocumentModel` 的直接断言，不依赖看图：

```
[selftest] --- 跨行状态机冒烟 ---
[selftest]   ✔ 第 0 行起始为干净状态
[selftest]   ✔ 块注释开启：第 2 行起始处于注释态
[selftest]   ✔ 块注释闭合：第 3 行起始回到干净状态
[selftest]   ✔ 跨行字符串开启：第 4 行起始处于字符串态
[selftest]   ✔ 跨行字符串闭合：第 5 行起始回到干净状态
[selftest]   ✔ 行注释里的引号不开启字符串
[selftest]   ✔ 行注释里的 */ 不结束注释
[selftest]   ✔ 第 2 行注释段一直延伸到 */（由起始注释态续行而来）
[selftest]   ✔ 第 2 行 */ 之后的 int 恢复为关键字
[selftest]   ✔ 编辑掉 /* 之后状态被失效并重算
```

**③ 编辑内核冒烟（14 条断言）** —— 通过 `window.GetVisualDescendants().OfType<EditorSurface>()`
取出真实控件，直接驱动 `HandleText` / `HandleKey`。
这两个方法之所以抽成 `internal`，是因为 Avalonia 的 `TextInputEventArgs` / `KeyEventArgs`
**没有公开构造函数**，没法在测试里伪造平台事件；抽出来之后自检驱动的是与真实按键**同一段**逻辑，
而不是另写一份仿制品：

```
[selftest] --- 编辑内核冒烟 ---
[selftest]   ✔ 输入后文本包含所输内容          ✔ 回车后行数 +1
[selftest]   ✔ 输入后置脏标记                 ✔ 回车插入的是文档换行风格
[selftest]   ✔ 输入后光标落在插入串之后        ✔ 撤销回车后行数复原
[selftest]   ✔ 输入后行数不变                 ✔ Ctrl+A 全选
[selftest]   ✔ 退格后文本还原                 ✔ Ctrl+End 跳到文末
[selftest]   ✔ 全部撤销后文本等于原文          ✔ 方向键下移一行
[selftest]   ✔ 重做 2 步后能全部撤销回来
[selftest]   ✔ 再次全部撤销后文本等于原文       [selftest] 全部通过 ✔
```

此外还会输出 `selection.png`，用于人工核对**选区渲染**（半透明覆盖 + 光标位置）——
这一项没有断言，只能看图。

**④ 能力在真实控件上的闭环（64 条断言）**

这一节的存在理由很具体：§7 里那些能力（彩虹括号 / 查找 / 跳转定义 / 补全，
本轮新增双击分词高亮 / 导航条 / 右键菜单）此前只有纯逻辑断言，
而纯逻辑断言**证明不了它们被接到了界面上** ——
配对算对了却没人调用它、候选算出来了却没画出来、查找改了 VM 却回流不到编辑区，
这些情形在纯逻辑断言下**全是绿的**。

做法：临时造一个 `.cs` 文件，用真实的 `EditorSurface` 走一遍全流程。

> **为什么必须另造 `.cs`、不能直接用自检样本 `sample-syntax.cs.txt`**：
> 语言按**扩展名**判定，`.txt` 落到 `Plain` —— 补全没有关键字词表、括号也不自动配对。
> 拿它在 Plain 文件上测这三项等于什么都没测（还会一路绿）。
>
> **样本写在系统临时目录而不是输出目录**：默认的 `--selftest-out` 就是工程内的 `selftest/`，
> 而 SDK 默认 glob `**/*.cs` —— 一个 `.cs` 落在工程目录里会被直接编进程序集，
> 下一次构建立刻 `CS0106`（开发时确实这么踩了一次）。

```
[selftest] --- 能力闭环冒烟（真实控件） ---
[selftest] 临时样本：…/Temp/LiteReader-selftest/feature-sample.cs
[selftest]   ✔ 光标在 ( 上 → 配到自己的 )
[selftest]   ✔ 光标在内层 ( 上 → 配到内层的 )
[selftest]   ✔ 光标在闭括号上 → 反向配回开括号
[selftest]   ✔ 光标不在括号上 → 没有配对（不会留着上一处的高亮）
[selftest]   ✔ 配对两端同色（外层 1/1、内层 2/2）
[selftest]   ✔ 内外层深浅不同（彩虹成立）
[selftest]   ✔ 行 8 的深度是第 7 行的 { 跨行带下来的（第 1 / 第 2 色）
[selftest] 彩虹括号 + 配对高亮 → features.png
[selftest] 彩虹括号（放大字号，便于肉眼核对深浅）→ features-rainbow-zoom.png
[selftest]   ✔ 向下查找命中定义处
[selftest]   ✔ 查找条显示「第 1 / 共 2 项」
[selftest]   ✔ 命中位置回流到了编辑区的光标
[selftest]   ✔ 查找下一个 → 跳到调用处
[selftest]   ✔ 查找上一个 → 回到定义处（不是原地打转）
[selftest]   ✔ 切换区分大小写后依然命中
[selftest]   ✔ 关闭查找条
[selftest]   ✔ 非纯文本语言下输入 ( 会自动补出 ()
[selftest]   ✔ 补出的确实是一对括号
[selftest]   ✔ 光标落在括号中间
[selftest]   ✔ 紧接着输入 ) 被消费（不重复插入）
[selftest]   ✔ 文本长度不变（跳过了已有的闭括号）
[selftest]   ✔ 光标越过闭括号
[selftest]   ✔ 撤销后自动补出的内容消失、文本逐字复原
[selftest]   ✔ 光标下的标识符被正确取出
[selftest]   ✔ 跳转定义命中（调用处 → 定义处）
[selftest]   ✔ 跳转后选中了目标词
[selftest]   ✔ 选区内容正好是目标标识符
[selftest]   ✔ 光标不在标识符上时不乱跳
[selftest]   ✔ 还没打字时没有候选
[selftest]   ✔ 继续输入 c 被接受
[selftest]   ✔ 待补全的前缀正是 Calc（不是被空格截断的片段）
[selftest]   ✔ 打字后弹出候选列表
[selftest]   ✔ 这个前缀下只应有 Calculate 一个候选（数量本身就是断言）
[selftest]   ✔ 候选里含文档内的函数 Calculate（标为函数）
[selftest]   ✔ 函数候选排在关键字之前（第一个就是它）
[selftest] 补全候选面板 → completion.png（1 个候选）
[selftest]   ✔ 确认函数候选后补出 ()
[selftest]   ✔ 光标落在 () 之间
[selftest]   ✔ 确认后候选面板收起
[selftest]   ✔ 文档（412 行）比视口（32 行）长 → 导航条生效
[selftest]   ✔ 导航条贴在控件右边缘（x=959 / 宽 972）
[selftest]   ✔ 滑块高度不超过轨道、且不小于 0（受最小高度保护）
[selftest]   ✔ 首行在文首 → 滑块贴顶
[selftest]   ✔ 按下滑块 → 进入拖拽态
[selftest]   ✔ 拖到最底 → 首行落到最后可滚位（380 / 380）
[selftest]   ✔ 拖回最顶 → 首行归 0
[selftest]   ✔ 拖到轨道正中 → 首行约在中间（190 ≈ 190）
[selftest]   ✔ 松开 → 退出拖拽态（否则之后滚轮一动就会跳回拖拽位置）
[selftest]   ✔ 松开后再拖动无效（拖拽态被正确清掉）
[selftest] 右侧导航条 → overview.png（412 行 / 可视 32 行，滑块 55.1 px）
[selftest]   ✔ 菜单项与 showEditorMenu 同序：复制/剪切/粘贴/全选/在命令提示符中打开/跳转到定义
[selftest]   ✔ 无选区 → 复制/剪切灰掉
[selftest]   ✔ 光标在标识符上 → 跳转到定义可用
[selftest]   ✔ 有选区 → 复制/剪切可用
[selftest]   ✔ 右键落在选区内 → 选区被保留（不会被这一下点没）
[selftest]   ✔ 选区里的整词也可作为跳转目标
[selftest]   ✔ 右键落在选区外 → 光标移到点击处并清掉选区
[selftest]   ✔ 光标不在标识符上 → 跳转到定义灰掉
```

**双击分词高亮**那一段（日志顺序在「跳转定义」和「补全」之间）单独摘出来 ——
它守的是「分桶对了、但渲染时查错了行」这类**跨层**错误：

```
[selftest]   ✔ 双击选中整个标识符
[selftest]   ✔ 双击后标记词就是它
[selftest]   ✔ 全文 2 处 Calculate 都被点亮（定义 + 调用）
[selftest]   ✔ 命中被分到了正确的两行上（第 2 行定义、第 9 行调用）
[selftest]   ✔ 命中的偏移正是那两处
[selftest]   ✔ 双击空白处不点亮任何词（否则会把空白/标点当词铺满全文）
[selftest]   ✔ （前提）先把标记点亮
[selftest]   ✔ 编辑文本后分词高亮自动清除（否则会点亮一堆错位的词）
[selftest]   ✔ 撤销后文本逐字复原
[selftest]   ✔ 单击正文清掉分词高亮
[selftest] 双击分词高亮 → marks.png（2 处命中）
```

> 「编辑后自动清除」这条断言看着多余，其实是踩过坑才加的：判据如果按 `DocumentModel.Version` 走，
> **按一次 `Ctrl+S` 就会把高亮抹掉** —— 保存只是抬了版本号、文本一个字没动。
> 现在的判据是「`Document.Text` 这个字符串实例还是不是当初收集标记时那一份」。
> 断言里的「（前提）先把标记点亮」也是有意写的：否则「编辑后变空」这条会因为起点就是空而永远通过。

#### 这一节里两个「差点蒙过去」的断言

| 现象 | 真实原因 | 修法 |
|---|---|---|
| 补全面板显示 **9 个候选**，而断言全绿 | 触发点写成了 `LineStartOffset(9) + 3`，落在 `"    Cal"` 的第 4 个**空格**上。输入 `c` 后前缀实际是 `"c"`，候选变成了全部 c 开头的关键字 —— 而 `Calculate` 也以 c 开头、又刚好排在第一位，于是「第一个就是 Calculate」这条断言轻松通过 | 落点改用 `LineEndOffset(9)`（不数位置）；并新增 `待补全的前缀正是 Calc` 与 `候选数 == 1` 两条断言，把假设直接钉住 |
| 深度失效断言「通过」，但改坏了也看不出来 | 原断言用「插入 `/*`」来验证失效：那会让整篇进注释态、深度**归零**，断言退化成「深度变了」而不是「深度对」 | 改成删掉一个 `{`，断言深度精确降 1（1→0、2→1） |

**这六条截图**（`features.png` / `features-rainbow-zoom.png` / `completion.png` / `selection.png`
/ `marks.png` / `overview.png`）
都是「色号算对 ≠ 画到了屏幕上」的兜底：取色键写错、主题字典缺键、候选面板画到看不见的地方，
断言全是绿的，只有看图才知道。其中 `features-rainbow-zoom.png` 特意把字号临时调到 20 ——
默认 13px 下相邻两个深度色的差别在两三百像素宽的缩略图里几乎看不出来。

新加的两张各管一件事：

- **`marks.png`** 看的是「底色真的铺在字底下，而不是盖在字上面 / 偏了一格」。
  这张图里第 2 行 `Calculate`（定义）与第 9 行 `Calculate(`（调用）两处都该被点亮，
  而第 10 行的 `Cal` **不该**被点亮 —— 后者正是「只收整词命中」的肉眼版验证。
- **`overview.png`** 看的是「滑块长度对不对」。样本 412 行、可视 32 行，
  滑块实测 **55.1 px**（轨道高 × 32/412 的量级），并且缩在轨道顶部；
  如果滑块粗看就是整根轨道，说明可视行数被当成了 0（分母错了）。

> **自检不会污染用户配置。** `--selftest` 会切主题、改字号、动窗口位置，这些都是诊断行为。
> `App.axaml.cs` 在进入自检分支时置 `AppConfigStore.SuppressWrites = true`，
> 所有落盘写入变成空操作。实测自检前后 `config.json` 字节完全一致。

**⑤ 非客户区与外壳（63 条断言）** —— 这一层是「外框跟随主题 + 图标资源」那一轮加的。
它要回答的是四个**互不替代**的问题，少一个都会留下哑雷：

| 套件 | 条数 | 回答什么问题 |
|---|---|---|
| 窗口外框配色（纯逻辑） | **22** | **颜色算对了没有** —— COLORREF 的 BGR 反读、相对亮度方向（亮色主题的标题栏必须亮）、标题文字与底色拉得开、边框与底色能分辨；另外「平台能力声明」**两个方向都断言**（Windows 上支持，非 Windows 上如实不做） |
| 图标资源 | **5** | **图真的能加载吗** —— 窗口图标非 null 且是 256×256、`AssetLoader` 能直接从**程序集资源**读出（不是 exe 旁边的文件 —— 单文件发布下旁边没有文件）、Linux 图标落在 `icons/hicolor/256x256/apps/` 下、`.desktop` 的 `Icon=` 与文件名**同源** |
| 窗口外框接线（真实控件） | **10** | **换主题时重施了吗** —— 切主题后读 `MainWindow.ChromeState`，它只在 `ApplyChrome` 里被写，所以「值跟着主题变」本身即证明订阅没断 |
| 外壳跟随主题（真实控件） | **26** | **外壳有没有跟着主题走** —— 见下 |

**「外壳跟随主题」这 26 条是本轮最值钱的一节**，因为它钉的是一个**看代码看不出来**的**既有** bug：
Avalonia 的资源查找里，**父字典自己持有的键优先于它的 `MergedDictionaries`**，
而 `ThemeService` 正是把主题字典塞进 `Application.Resources.MergedDictionaries` ——
于是只要 `App.axaml` 里恰好也定义了同名键（原本确实定义了 `EditorBackground` 等 14 个，
理由是「设计期与首帧不缺资源」），主题值就被**永久遮住**，
`{DynamicResource EditorBackground}` 恒等于那个默认色 `#282C34`。

症状很有欺骗性：**编辑器正文跟着主题变**（它是自绘的，直接调 `ThemeService.Brush`，不走资源系统），
**外壳不跟**（窗口底 / 状态栏 / 分隔条 / 左侧栏 / 标签栏全停在默认色）。
本轮把系统标题栏按主题上色之后，「浅色外框 + 一整块深色内容」的割裂才把它暴露出来。
修法是**删掉 `App.axaml` 里那份默认色**（首帧并不缺色：`App.OnFrameworkInitializationCompleted`
里先 Load 配置 → `new ThemeService` → `RestoreFromConfig`（内部 `Apply`）→ 才 `new MainWindow`）。

> ★ 这一节必须**对 5 套主题逐一验**，不能只验默认主题：原本的默认值 `#282C34`
> 恰好就是 One Dark Pro 的底色 —— **只验默认主题的话，这个 bug 会全绿通过**。
> 断言读的是「外壳控件的 `Background`」对比「`ThemeService.ColorOf` 直接查到的值」，
> 两条路一个走资源系统、一个绕过它，一旦再次遮蔽立刻分叉。

> 这一层还有一条**故意不写的断言**：`TabControl.Background` 实测在 5 套主题下恒为 `#FFFFFF`
> （Fluent 的默认值），但它的模板**根本不绘制**这个属性 —— 真实截图里标签栏画的就是主题底色。
> 拿一个不参与绘制的属性去断言，只会得到一条**永远红的假警报**，而假警报比没有断言更糟：
> 它会让人开始无视红色。所以那里只打印留痕，像素结论交给下面的抓图工具。

**非客户区与外壳的像素级验收**（属性断言够不到的那一半）：

```
spikes/winshot/grab.py       抓真实窗口（DWM 的 EXTENDED_FRAME_BOUNDS 才是肉眼那个框）
spikes/winshot/probe_chrome.py  量标题栏/三边外框/标题文字/窗口图标的像素色
spikes/winshot/probe_shell.py   量主题截图里状态栏/左侧栏/分隔条的像素色（扫描式，不写死坐标）
spikes/winshot/probe_tokens.py  数编辑区里实际渲染出的 token 色
spikes/winshot/parse_selftest.py 按 UTF-8 解析 selftest 日志、按套件统计 ✔/✘
```

为什么必须有这一套：`--selftest` 的截图走 Avalonia 的 `RenderTargetBitmap`，
它**只抓客户区** —— 系统标题栏、外边框、标题栏里的小图标全在**非客户区**，
而那恰好是本功能唯一的验收点。

**⑥ 工具层（91 条断言）** —— 桌面集成那几样（Ctrl+滚轮 / 窗口外框接线 / 外壳跟随主题 /
单实例转发 / 外部终端 / 文件关联）有一半是**不能真跑**的：真跑就会去写注册表、起终端进程、开第二个窗口。
所以这一层的原则是「**把可断言的部分单独摘出来，别为了测它去产生副作用**」：

| 测什么 | 怎么测 | 为什么这样测 |
|---|---|---|
| **Ctrl+滚轮调字号**（9 条） | 打真实 `EditorSurface` 的 `HandleWheel` | 与光标可见性、行高缓存、状态栏镜像共用状态，纯逻辑测不到「字号改了但状态栏没跟上」 |
| **窗口外框接线**（10 条） | 切主题后读 `MainWindow.ChromeState` | 颜色对不对由纯逻辑那节管，这里只管「订阅有没有断」；断了的后果是**换主题后外框还留着上一套的颜色**，不报错 |
| **外壳跟随主题**（26 条） | 逐主题读窗口底 / 状态栏 / 分隔条的 `Background`，与主题字典直接查到的值比对 | 见 ⑤；这是「资源遮蔽」类 bug 唯一能被自动抓住的地方 |
| **单实例 + 转发**（14 条） | 纯函数（判重、编解码）**+ 进程内完整收发回路** | 「起监听 → 发 → 收到」可以在一个进程里跑通，没必要真开两个进程；而消息编解码一旦写错是**静默**的（收到的路径和发出去的不一样） |
| **外部终端**（17 条） | 只断言三平台候选清单的**结构** | 真起终端在 CI 上不可行；但 Linux/macOS 那两份清单在本机永远走不到，结构断言是它们唯一的保护 |
| **文件关联**（15 条） | 只断言纯函数（键路径 / 命令行 / desktop 内容） | 真跑会写 HKCU 注册表、跑 xdg-mime —— 自检不该动系统状态 |

这一层有两条断言值得单独说，它们守的是**设计决策**而不是实现正确性：

```csharp
// 句柄必须被静态列表挂住 —— 一旦被 GC 回收，名字就没了，
// 「单实例保护」会凭空失效，而且完全不报错。
Check("持有中：该名字确实已被占用（句柄没被 GC 掉）", SingleInstance.ProbeNameTaken(mutexName));

// 只进「打开方式」列表，绝不抢默认关联 —— 否则会顶掉用户机器上的 VS Code / Notepad++
Check("没有任何扩展名被注册成默认关联", Extensions.All(e => WindowsOpenWithKey(e) != $@"Software\Classes\{e}"));
```

### 5.4 两个「只能靠看图/对拍」才能发现的坑

**（一）纯静默的渲染 bug：`FormattedText.Width` 会裁掉行尾空白**（Skia 行为）。

编辑区为了精确光标定位与命中测试，把一行切成「同质段」（全窄 / 全宽 / 制表符三类）
并按段累加 x 坐标。于是纯空格段 / 缩进段 / Tab 展开的空格段宽度会被量成 **0**，
x 坐标从这一段开始**整体左移**，渲染结果就是：

```
public static class   →   publicstaticclass     行首缩进整段消失
```

**编译通过、断言全绿、日志干净** —— 因为它不是逻辑错误，只是几何量错了。
修法（`EditorSurface.AdvanceWidth`）：末尾补一个哨兵字符 `"M"` 再减去它的宽度。

```csharp
private double AdvanceWidth(string display, FormattedText own)
{
    if (display.Length == 0 || !char.IsWhiteSpace(display[^1])) return own.Width;
    var withSentinel = new FormattedText(display + "M", ..., Brushes.Black);
    return Math.Max(0, withSentinel.Width - _sentinelW);
}
```

**（二）增量行索引的三处 CRLF / 半行边界。** 差分对拍逐步比对抓出来的，
每一个的表现都只是「差一行」或「差一个 `\r`」：

| # | 触发条件 | 症状 | 修法 |
|---|---|---|---|
| 1 | region 末尾正好落在换行符上、且后面还有 tail | 行数多 1 | `keepTrailing = !hasTail \|\| trailingIsPartial` |
| 2 | region 末尾是**半行**（不含换行），物理上与 tail 首行是同一行 | 行数虚增 1 | `mergeTailFirst` / `tailKeep`，并把 tail 首行长度并进上一行 |
| 3 | 半行合并到一个**空** tail 首行时，末尾的 `\r` 该不该折 | 单行内容差一个 `\r` | 看终止符是裸 LF 还是 CRLF：`regionEndNew >= _text.Length \|\| _text[regionEndNew] == '\n'` |

第 3 个坑改了两轮才对 —— 第一版条件写得太窄（要求 `tailKeep == 0`），
漏掉了「tail 还有更多行、但首行是空行」这一情形。

### 5.5 一次性验收（`selftest/verify.log`）

把三个诊断按**有讲究的顺序**串成一条验收链，输出留在 `selftest/verify.log`：

```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8   # ★ 见下：不设这一句，✔ 和 ✘ 会长得一模一样
Set-Location <工程>\bin\Release\net10.0\win-x64
dotnet LiteReader.dll --selftest <工程>\selftest\sample-syntax.cs.txt --selftest-out <工程>\selftest
dotnet LiteReader.dll --fuzz 20000 --fuzz-seed 7
Set-Location <工程>
dotnet build LiteReader.Avalonia.csproj -c Release -v m     # ← 放在最后，本身就是一条断言
```

> 注意 `win-x64` 这一段：本工程按 RID 构建，产物在 `bin\Release\net10.0\win-x64\`，
> 不是 `bin\Release\net10.0\`。

**发布形态改为 AOT 后，验收必须同时在「要发布的那个 exe」上跑一遍**（2026-09-22 起）：

```powershell
$exe = "<工程>\dist-win-x64\LiteReader.exe"
Start-Process -FilePath $exe -PassThru -Wait `
  -ArgumentList @("--selftest", "<工程>\selftest\sample-syntax.cs.txt", "--selftest-out", "<工程>\selftest") `
  -RedirectStandardOutput "<工程>\selftest\aot.stdout.log" -RedirectStandardError "<工程>\selftest\aot.stderr.log"
Start-Process -FilePath $exe -PassThru -Wait `
  -ArgumentList @("--fuzz","20000","--fuzz-seed","7") `
  -RedirectStandardOutput "<工程>\selftest\aot-fuzz.stdout.log" -RedirectStandardError "<工程>\selftest\aot-fuzz.stderr.log"
```

> ★ **必须用 `Start-Process -RedirectStandardOutput`，不能写 `& $exe 1> file`。**
> AOT 产物是 `WinExe`（`SUBSYSTEM:WINDOWS`，没有控制台）：
> `Start-Process` 的重定向是把句柄交给子进程、原始字节直接落盘，**能拿到输出**（实测 24 KB）；
> 而 PowerShell 自己的 `1>` 会经 `Out-File` 转一手，结果是**空文件**（看起来像「程序没输出」），
> 而且写出来的还是 UTF-16，用按 UTF-8 解析的统计脚本会数出 0 个 `✔` ——
> **用 `count_any.py` 这类「自动试编码」的脚本读，别有用心地指定编码。**

已实测（2026-09-22，同一份 AOT 产物）：`--selftest` **305 ✔ / 0 ✘ / 19 小节**、
截图报告 11 个文件；`--fuzz 20000 --fuzz-seed 7` 全部通过。

> ★ **少传样本文件不会报错，只会静默少跑 23 条断言**（2026-09-22 实测踩到）。
> `--selftest` 后面的**样本文件路径不是可选的**：省掉它，程序照常起窗口、照常 `exit=0`，
> 但窗口里没有文档 → 没有编辑区 → 两节直接打印「跳过」：
>
> ```
> [selftest] 编辑冒烟：跳过（没有打开文档，故没有编辑区）
> [selftest] --- Ctrl+滚轮调字号 / 滚轮修饰键冒烟 ---
> [selftest]   跳过（没有找到编辑区）
> ```
>
> 结果是 **282 ✔ / 18 小节 / 10 张截图**，而不是 305 / 19 / 11 ——
> 少的是 `编辑内核冒烟`（14 条，**整节不出现**）与 `Ctrl+滚轮调字号`（9 条，本节 0 条）。
> **退出码与 `✘` 计数都不会有任何异常**，所以「跑过了、没报错」不等于覆盖完整。
> → 验收时要**逐节核对断言数**（用下面那段脚本），并确认日志里没有「跳过」行。

> ★ **必须在重定向前设 `[Console]::OutputEncoding = UTF8`**。
> 程序已经把自己那边的 stdout 设成 UTF-8 了，但 PowerShell 侧的**解码**用的是控制台代码页：
> 解码错了之后 `✔`（U+2714）与 `✘`（U+2718）会变成**同一个乱码字**（`鉁?`），
> 于是「失败 1 项 ✘」在日志里读起来和「通过」没区别 —— 本轮就因此差点把一条红断言当成绿的。
> 稳妥起见用 `spikes/winshot/parse_selftest.py` 解析：它按 UTF-8 读、按套件分节计数，
> 并会跳过自带 `✘` 的汇总行。

> `--selftest-out` 建议直接写**绝对路径**：这个参数是按 `Environment.CurrentDirectory` 解析的，
> 而用 `dotnet <绝对路径>\LiteReader.dll` 启动时当前目录**不一定是 dll 所在目录** ——
> 一旦落到别处，截图和日志就散在两个文件夹里（本轮就发生过一次，
> 新截图落在仓库根的 `selftest/`、老截图还在工程内）。

**为什么把 `dotnet build` 放在最后**：`--selftest` 会在输出目录里造样本文件，
而这个「输出目录」默认就是工程内的 `selftest/`。曾经的 bug 是样本用 `.cs` 扩展名落进去、
被 SDK 的 `**/*.cs` glob 编进程序集 → **下一次构建必挂 `CS0106`**。
把构建排在自检之后，等于每轮验收都顺手证明「这次自检没有污染工程目录」：

```
[selftest] 全部通过 ✔                        selftest exit=0
[fuzz] 全部通过 ✔                            fuzz exit=0
已成功生成。  0 个警告  0 个错误              build（自检之后）exit=0
✔ = 305    ✘ = 0        selftest 输出目录内 .cs 残留数 = 0      PNG = 11 张
```

**顺便核对断言数**：日志里的 `✔` 行数就是要写进文档的数字（当前 **305**）。
本文档的数字随断言增删会漂移，改完断言记得回来对一次。
按套件分别数一遍更稳（一眼就能看出是哪一节漂了），脚本如下：

```powershell
$lines = [System.IO.File]::ReadAllLines("$proj\selftest\verify.log", [System.Text.Encoding]::UTF8)
$cur='(前导)'; $n=0
foreach($l in $lines){
  if($l -match '^\[selftest\] --- (.+?) ---'){ if($n -gt 0){ "$cur = $n" }; $cur=$Matches[1]; $n=0 }
  elseif($l -match '\u2714'){ $n++ }
}
if($n -gt 0){ "$cur = $n" }
```

本机实测输出（与 §5.3 的表格一一对应）：

```
跨行状态机冒烟 = 13              彩虹括号 / 嵌套深度冒烟 = 13
括号配对冒烟 = 12                查找冒烟 = 12
跳转定义冒烟 = 7                 代码补全冒烟 = 10
双击分词高亮冒烟 = 17            右侧导航条几何冒烟 = 14
窗口外框配色冒烟 = 22            主题键完整性 = 10        ── 纯逻辑合计 130
图标资源冒烟 = 5                 编辑内核冒烟 = 14
能力闭环冒烟（真实控件） = 64    ── 真实控件合计 83
Ctrl+滚轮调字号 / 滚轮修饰键冒烟 = 9
窗口外框：真实窗口接线 = 10      外壳跟随主题（App.axaml 不再遮蔽合并字典） = 26
单实例 / 文件转发冒烟 = 14       外部终端的候选清单冒烟 = 17
文件关联（纯函数）冒烟 = 16      ── 工具层合计 92
                                        总计 305
```

> 分节靠 `[selftest] --- <名字> ---` 这一行。**尾部的 `---` 前面必须留一个空格**
> （`... 字典） ---` 而不是 `... 字典）---`）：上面那条正则是 ` --- ` 收尾的，
> 少了空格这一节就不会被识别，它的断言会被算进**上一节** —— 总数还是对的，
> 但「哪一节漂了」这个好处就没了。本轮加小节时就踩了一次。

### 5.6 单实例的真机验证（自检覆盖不到的那一半）

工具层里的单实例/文件转发，自检只能测到**进程内**的收发回路。
「两个真实进程之间到底有没有互相打扰」必须另跑一遍 —— 而这一步恰好是最能暴露问题的地方：

```powershell
# 注：诊断参数一律绕开单实例（见 Services/StartupFiles.cs），所以这里必须直接跑 exe
$p1 = Start-Process LiteReader.exe -ArgumentList "$tmp\a.cs" -PassThru   # 第一实例
Start-Sleep 5
$p2 = Start-Process LiteReader.exe -ArgumentList "$tmp\b.cs" -PassThru   # 第二实例：应转发后自杀
$p2.WaitForExit(8000); $p2.ExitCode
```

实测（2026-09-18，win-x64）：

| # | 情形 | 期望 | 实测 |
|---|---|---|---|
| 1 | 起第一实例（带 `a.cs`） | 1 个进程 | **1** ✔ |
| 2 | 再起一个带 `b.cs` 的实例 | 转发后退出、进程数不变 | 退出码 **0**，进程数 **1** ✔ |
| 3 | （承上）等 2 秒 | 第一实例没被回调搞崩 | 仍存活 ✔ |
| 4 | 起一个**不带文件**的实例 | 只叫窗口、进程数不变 | 退出码 **0**，进程数 **1** ✔ |
| 5 | `--fuzz 200 --fuzz-seed 7` | 绕开单实例、正常跑完 | 退出码 **0** ✔ |

第 3 条是这里最值钱的一条：**如果管道回调里抛了异常，Avalonia 会直接把第一实例干掉**
（回调跑在 UI 线程上）。「A 还活着」同时证明了「消息被正确解析」和「回调没炸」。

---

## 6. 工程结构

```
src/LiteReader.Avalonia/
├── LiteReader.Avalonia.csproj     ApplicationIcon=Assets\appicon.ico + AvaloniaResource=Assets\**
├── Program.cs                     入口 / AppBuilder 配置 / 无窗口诊断提前返回 / **单实例门禁**
├── App.axaml(.cs)                 应用装配、配置载入、命令行文件参数、**单实例接收端**
│                                  ★ App.axaml 里**故意不放任何默认主题色**，见 §7.5
├── Assets/
│   ├── appicon.ico                多尺寸图标 → csproj 的 ApplicationIcon（进 exe 的 PE 资源段）
│   └── icon.png                   同源导出的 256×256 → Window.Icon + Linux 图标安装
├── Models/
│   ├── DocumentModel.cs           文本 + **增量行索引** + 操作式撤销栈 + 跨行着色状态（含括号深度）+ 函数名索引
│   ├── SyntaxHighlighter.cs       逐行分词着色 + **彩虹括号** + SQL 词括号（行起始状态 → 扫描 → 行末状态）
│   ├── LanguageMode.cs            按扩展名判语言（决定 SQL 词括号、补全词表、是否自动配对）
│   ├── BracketMatcher.cs          括号配对（同类型栈扫描 + SQL 的 BEGIN/END/CASE）
│   ├── TextSearch.cs              查找（循环查找 / 计数 / 序号），纯函数
│   ├── DefinitionFinder.cs        跳转定义（单文件启发式打分）
│   ├── CompletionEngine.cs        补全候选（文档内函数名 + 按语言的关键字表）
│   ├── WordMarker.cs              **双击分词高亮**：整词边界 + 全文档命中收集（按行分桶），纯函数
│   └── OverviewBar.cs             **右侧导航条几何**：滑块高度/位置 ↔ 首行 的互逆换算，纯函数
├── Services/
│   ├── ThemeService.cs            5 套主题切换 + 配色查询（静态字典工厂）+ `ColorOf` 取色（给 DWM 用）
│   ├── AppConfig.cs               配置持久化（XDG 目录 + JSON 源生成器）
│   ├── EditorSettings.cs          全局字号/制表宽/补全开关（静态单例 + 事件广播）
│   ├── StartupFiles.cs            启动参数分类（诊断开关 / 真实存在的文件路径）
│   ├── WindowChrome.cs            **窗口外框跟随主题**（DWM 四属性：深色标志 + 3 个颜色 + COLORREF 反读）
│   ├── SingleInstance.cs          **单实例 + 文件转发**（命名互斥体判重 + 命名管道收发）
│   ├── FileAssociation.cs         **注册为「打开方式」**（Win 注册表 / Linux .desktop + 图标安装 / macOS 说明）
│   └── TerminalLauncher.cs        **在文件所在目录打开终端**（三平台候选清单 + 有序尝试）
├── ViewModels/
│   ├── DocumentTabViewModel.cs    含光标/选区/滚动视图状态
│   ├── FileNodeViewModel.cs       文件树（懒加载、隐藏文件、符号链接成环）
│   └── MainWindowViewModel.cs     标签/主题/缩放/状态栏 + **查找** + **工具（终端 / 文件关联）**
├── Views/
│   ├── MainWindow.axaml(.cs)      外壳（Icon=/Assets/icon.png）+ 查找条 + 导航/工具菜单 +
│   │                              文件对话框注入 + 树交互 + 隧道路由的 F12/Esc + **ApplyChrome 接线**
│   └── EditorSurface.cs           自绘可编辑编辑器（行号槽 + 分段着色 + 配对高亮 + **分词标记底色** +
│                                  **右侧导航条** + 自绘补全面板 + **右键菜单** +
│                                  键盘/输入/剪贴板/滚轮内核 + 括号自动配对）
├── Themes/                        5 套配色 ResourceDictionary（含 Rb0..Rb5 等 **18** 个新键）
│   ├── OneDarkPro.axaml  OneLight.axaml  VsCodeDark.axaml
│   ├── IntelliJIdea.axaml  Amoled.axaml
│   └── ThemeDictionaries.cs       5 个 partial class（x:Class 分区类，供静态工厂 new 出来）
├── Diagnostics/
│   ├── EditFuzz.cs                --fuzz 差分对拍（行索引 + 跨行着色状态）/ --bench-edit 基准
│   └── SelfTest.cs                --selftest：视觉回归 + 纯逻辑断言 + 编辑内核 + 能力闭环 + 工具层
└── selftest/
    ├── sample-syntax.cs.txt       着色自检样本（跨行注释、CJK、制表符、未闭合字符串）
    ├── verify.log                 一次性验收日志：selftest + fuzz + 构建复验（见 §5.5）
    ├── chrome/                    非客户区/外壳的验收留痕（手工跑，不在上面 11 张之列）
    │   ├── window-one-light.png   真实窗口全图（标题栏 + 外边框 + 图标都在里面）
    │   ├── chrome-pixels.txt      probe_chrome.py 的原始读数
    │   └── shell-pixels.txt       probe_shell.py 的原始读数
    └── *.png                      自检产物（共 11 张）：theme-*.png ×5 / selection.png / features.png /
                                   features-rainbow-zoom.png / completion.png / marks.png / overview.png

spikes/winshot/                    「抓真实窗口 + 像素取样」工具（非客户区验收，见 §5.3 ⑤）
├── grab.py                        按标题找窗口 → 置前 → 抓窗口矩形（用 DWM 的可见外框，不是 GetWindowRect）
├── probe_chrome.py                量标题栏 / 三边外框 / 标题文字 / 窗口图标的像素色
├── probe_shell.py                 量主题截图里状态栏/左侧栏/分隔条的像素色（扫描式，不写死坐标）
├── probe_tabs.py / probe_tokens.py 标签栏那条带 / 编辑区实际渲染出的 token 色
├── ico2png.py                     从多尺寸 ICO 抽出指定尺寸存 PNG
└── parse_selftest.py              按 UTF-8 解析 selftest 日志、按套件统计 ✔/✘
```

### 几个设计决定的原因

- **编辑区自绘、外壳用控件**：实测 Avalonia `TextBox` 给 19.3 MB 文本赋值只花 0 ms
  （赋值是惰性的），但没有任何证据表明它能流畅显示 10 万行；而分段着色、行号槽、
  当前行高亮、脏矩形增量重绘这些编辑器必需能力，框架文本框都不提供。
- **主题版本与字号走静态 + 事件广播**（`ThemeService.Changed` / `EditorSettings.Changed`）：
  自绘控件在 `OnAttachedToVisualTree` 订阅、`OnDetachedFromVisualTree` 退订。
  好处是 XAML 里不用写 `$parent[Window].((vm:MainWindowViewModel)DataContext).XXX`
  这类长绑定，模板层保持干净、也不容易在编译期绑定上报错。
- **主题字典改成静态分区类**：换主题只需换一个字典实例，新增主题仍不用改 XAML
  （在 `ThemeDictionaries.cs` 加一个 `partial class` + 在 `Catalog` 加一行），
  同时消除了 `IL2026` 裁剪告警 —— 此前主题字典用 `AvaloniaXamlLoader.Load(Uri)` 动态加载，
  触发 `IL2026: …RequiresUnreferencedCode`（裁剪器看不见那些 XAML 里的资源）；
  改成静态资源字典 + 分区类之后，全部引用在编译期就是可见的。
- **文件对话框能力由 View 注入给 VM**（`PickFilesAsync` / `PickFolderAsync` 委托）：
  VM 因此不依赖 `TopLevel` / `StorageProvider`，可单测、也不绑死某个平台的对话框实现。
- **光标/选区状态用双向绑定的 `StyledProperty` 回流到 VM**：
  Avalonia 自绘控件没有 `SelChanged` 这类事件可订阅，直接暴露成属性让 XAML 绑定，
  状态栏的「行:列」由 `partial void OnCaretOffsetChanged` 触发刷新，
  比再发明一套事件、再在 View 里手工同步更省事。
- **平台差异全部收在 `Services/` 下**（`SingleInstance` / `FileAssociation` / `TerminalLauncher`）：
  VM 与 XAML 里**一句平台判断都没有**，`OperatingSystem.IsWindows()` 这类判断只出现在这三个文件里。
  好处不只是整洁 —— 自检可以绕过有副作用的那一层，只断言纯函数（见 §5.3 ⑤）。
  注册表访问虽然能在三平台编译（`Microsoft.Win32.Registry` 在 net10.0 的 ref pack 里），
  但非 Windows 上运行会抛 `PlatformNotSupportedException`，所以必须由 `IsWindows()` 守住 ——
  编译器也会盯着这一点（CA1416）。

### 编辑内核的三个关键设计

**① 操作式撤销栈（对照 `LiteReader.cpp:248` 的 `struct EditStep`）**

```csharp
private readonly record struct EditStep(int Start, string Del, string Ins);
```

撤销 = 删掉它插入的串、填回它删除的串；重做 = 反向。比整篇快照省内存得多。
另外做了**连续输入合并**：单字符插入 / 退格在 700 ms 窗口内且位置相邻时合并成一步历史，
否则敲「abcdef」会产生 6 步撤销。

**② 脏标记 = 撤销栈深度比较，而不是粘性 bool**

```csharp
public bool IsDirty => _undo.Count != _cleanUndoDepth;
```

粘性 bool 的问题在于「全部撤销回原样之后仍然显示为脏」。
用深度比较则天然正确：撤销回到保存点自然就干净了。这算是顺手修掉了 C++ 版的一个小毛病。

**③ 增量行索引：只重切受影响区间 + 尾段整体平移**

全文重扫在 10 万行下约 20 ms/按键（打字会顿），所以改成：

- 算出受影响的 `firstLine` / `lastLine`，只对这块 region 重新切分
- 尾段行索引**整块平移**（`delta` 加到位移上），用 `Array.Copy` 就地更新
- **容量与行数分离**（`int[] _lineStart = new int[16]` + `_lineCount`），
  避免每次编辑都 `new int[行数]`
- `Array.Copy` 允许源/目标重叠（语义同 `memmove`），所以插入与删除两个方向都安全

三处致命的 CRLF / 半行边界见 §5.4。

---

## 7. 与 LiteReader.cpp 的对照

| 能力 | C++ 单文件版 | 本工程 |
|---|---|---|
| 代码着色 | `scanLine` 手写状态机 | `SyntaxHighlighter`（同算法，`ReadOnlySpan` 零分配，实测 432 MB/s） |
| 跨行块注释 / 跨行字符串 | `lineInBC` / `lineInSrv` / `lineBsQ` 三个状态位 | `LineState` 行起始/行末状态 + `DocumentModel` 懒推进缓存（同语义） |
| **彩虹括号** | `COLORREF rb[6]` + `depth % 6` | ✅ `LineState.Depth` 跨行嵌套深度 + `Token.Color`（见 §7.1） |
| **括号匹配** | `findMatch()` | ✅ `BracketMatcher`（同类型配对 + SQL 的 `BEGIN/END/CASE` 词括号） |
| **查找** | `doFind()` | ✅ `TextSearch` + 顶部查找条（`Ctrl+F` / `F3` / `Shift+F3`，循环查找、`Aa` 大小写、增量查找） |
| **跳转定义** | `gotoDefinition` / `scoreDefinition` | ✅ `DefinitionFinder`（并修掉了原版两条死的打分规则，见 §7.2） |
| **代码补全** | 独立 `WS_POPUP` 弹窗 | ✅ `CompletionEngine` + 自绘候选面板 + 括号/引号自动配对 |
| **双击分词高亮** | `g_markWord` / `g_markRanges` + `markBg()` 底色 | ✅ `WordMarker`（整词命中 + 按行分桶）+ Render 里一层底色（见 §7.4） |
| **右侧滑动导航条** | 系统 `WS_VSCROLL` + `SetScrollPos` / `WM_VSCROLL` | ✅ 自绘 `OverviewBar`（滑块高度 = 可视占比，可拖动跳转；见 §7.4） |
| 长文本 | 行位图缓存 + 脏矩形 | 按行缓存 `FormattedText` + 可见行窗口 |
| 多主题 | 主题表（数组） | 5 个 `ResourceDictionary`，运行时切换 |
| 多标签 | 自绘标签栏 | `TabControl` |
| 文件夹浏览 | 自绘树 | `TreeView`（懒加载） |
| 编码识别 | 手写 + Win32 API | `DocumentModel.Decode`（BOM → 严格 UTF-8 → GBK） |
| 菜单/右键菜单 | 自绘 + `TrackPopupMenu` | `Menu` / `ContextMenu`（**项序与原版 `showEditorMenu` 逐项对齐**，见 §7.4） |
| 轻量编辑 | 已实现 | ✅ 已实现（光标/选区/插入删除/剪贴板/双击选词/三击选行） |
| 撤销/重做 | 操作式撤销栈 | ✅ 已实现（`EditStep` 同设计 + 连续输入合并 + 深度式脏标记） |
| **字号调节** | `WM_MOUSEWHEEL` + `GetKeyState(VK_CONTROL)`，每格 ±1，夹在 9~28 | ✅ `HandleWheel`（详见 §7.3）；范围 8~32 |
| **单实例 + 传递文件** | `CreateMutexW` + `FindWindowW` + `WM_COPYDATA` | ✅ 改**命名互斥体 + 命名管道**（`FindWindow` 在 Wayland 上不可用，见 §7.3） |
| **文件关联** | `HKCU\Software\Classes\Applications\...` + `OpenWithList` | ✅ 同原版（Win）；Linux/.desktop、macOS/说明文件（见 §7.3） |
| **终端集成** | `ShellExecute` + 枚举已有终端窗口 | ⚠️ 同「有序尝试」形状，但**不枚举/不复用已有终端窗口**（Wayland/macOS 都做不到，见 §7.3） |
| 体积 | ≈740 KB | **37.4 MiB**（Windows x64 Native AOT：exe 21.6 + 3 个原生 dll） |
| 启动 | 双击即开 | **≈0.7 s**（进程起→窗口可见；托管形态是 1.6–1.8 s） |
| 常驻内存 | < 3 MB | **≈139 MB**（Skia + GPU 缓冲；托管形态 ≈163 MB） |

**必须接受的代价**：体积、启动、内存三项都比 C++ 版差一个量级。
这是「换到托管 + 现代 UI 框架」的入场费，换回来的是可维护性、
跨平台代码结构、以及能在 C# 生态里持续演进。
（**2026-09-22 修订**：发布形态已改为 Windows x64 Native AOT，启动与内存这两项比原来的
托管单文件方案明显改善 —— 见《性能评估-2026-09-22.md》§2 与 §6.3；体积基本持平。）

> 编辑延迟另有一项代价：大文件（10 万行）下单次按键约 8 ms，
> 其中 8 ms 几乎是 `string.Concat` 重建整串文本的成本（见 §5.2）。
> C++ 版用 `std::wstring` 有同样的问题，只是常量因子小 —— 真要根治得换 piece table。

### 7.1 彩虹括号：三件必须做对的事

对照 `LiteReader.cpp` 的 `rb[6]` + `depth % 6`。三个点是**做错了就静默失效**的：

**① 闭括号取的是「减一后」的深度色，不是当前深度色。**

```csharp
if (c is '(' or '[' or '{') { Emit(…, TokenKind.Bracket, depth % 6); depth++; }
if (c is ')' or ']' or '}') { int d = depth > 0 ? depth - 1 : 0; Emit(…, d % 6); if (depth > 0) depth--; }
```

只有这样才能保证**一对括号同色**：`{` 在深度 d 时取第 d 色，配对的那个 `}` 在 `depth == d+1`
时取第 d 色。反过来写的话每一对括号都是深浅配错、整篇看起来像随机上色。

孤立闭括号（`depth == 0` 时的 `)`）按第 0 色画，并且**不把 depth 减成负数** ——
否则后面所有行的彩虹索引会整体错位，症状是「从某一行起颜色全乱」。

**② 深度必须是跨行状态，与块注释共用同一套懒推进机制。**

否则每个跨行的函数体都会从第 0 色重新开始 —— 一行之内看着没问题，整体一看就不对。
实现上就是把 `Depth` 作为 `LineState` 的第三个字段：

```csharp
public readonly record struct LineState(bool InComment, char InString, int Depth = 0);
```

`DocumentModel.EnsureState` 逐行推进时把它当输入输出传递（`ScanLine(GetLine(m), st, null, Language)`）。

**③ 色号必须参与 token 合并判断。**

这个最隐蔽。token 合并的初衷是把相邻同类段并起来少画点文字：

```csharp
if (last.Kind == kind && last.Color == color && last.Start + last.Length == start) { 合并 }
```

如果漏掉 `last.Color == color`，`((` 会被并成**一段**（第 0 色 + 第 1 色），
结果就是「连续两个开括号时彩虹断一档」—— 文本内容完全正常、程序也不报错。

> **SQL 的词括号**：`BEGIN` / `CASE` 当左括号、`END` 当右括号，与 `()[]{}` 走同一套深度。
> 这样 `BEGIN…END`、`CASE…END` 能被当成一对高亮，而不是「找不到匹配」。
> 判定是**大小写敏感**的（全大写才算），所以 C# 里的小写 `case` 不会被误当块括号。
> 这需要 `LanguageMode`（按扩展名判定，见 `Models/LanguageMode.cs`）。

**主题键**：每套主题必须提供 `Rb0`..`Rb5`（六个彩虹色）+ `EditorMatchBackground`（配对高亮）
+ `CompletionBackground/Border/Foreground/SelectionBackground/SelectionForeground`（候选面板）
+ 分词/导航条那三个（见下表）+ **窗口外框那三个**（`WindowCaptionBackground` / `WindowCaptionForeground` /
`WindowBorder`，见 §7.5），**共 18 个**。取值直接照搬 `LiteReader.cpp` 各主题的 `COLORREF rb[6]`
（`COLORREF` 是 `0x00BBGGRR`，要反着读）。`ThemeKeySmoke` 逐主题、逐键断言它们存在且**六个彩虹色互不相同** ——
漏一个键不会有任何报错，`ThemeService.Brush` 会安静地返回兜底色，
表现只是「这一套主题下括号颜色不对」。

本轮新增的三个键，`EditorMarkBackground` 直接取自 C++ 各主题的 `markBg` 字段（同样是 BGR 反读）：

| 主题 | `EditorMarkBackground` | `EditorOverviewBackground` | `EditorOverviewThumb` |
|---|---|---|---|
| One Dark Pro | `#1A4046` | `#21252B` | `#4B5263` |
| One Light | `#C0E6BF` | `#EDEDED` | `#C2C2C4` |
| VS Code Dark | `#3A3232` | `#252526` | `#424242` |
| IntelliJ IDEA | `#46494A` | `#313335` | `#5A5A5A` |
| 极致黑 AMOLED | `#2A2A2A` | `#141414` | `#3A3A3A` |

> 后两个键（导航条）在原版里没有对应物 —— 原版用的是系统滚动条，配色由 Windows 主题决定。
> 这里是自绘，所以三平台都得自己给色。

再往后一轮又加了**窗口外框那三个键**（供 §7.5 的 DWM 用）：

| 主题 | `WindowCaptionBackground` | `WindowCaptionForeground` | `WindowBorder` |
|---|---|---|---|
| One Dark Pro | `#21252B` | `#ABB2BF` | `#3E4451` |
| One Light | `#F0F0F1` | `#383A42` | `#D8D8DA` |
| VS Code Dark | `#323233` | `#CCCCCC` | `#3C3C3C` |
| IntelliJ IDEA | `#313335` | `#A9B7C6` | `#4E5254` |
| 极致黑 AMOLED | `#0A0A0A` | `#E0E0E0` | `#2A2A2A` |

> 这三个键也不能只断言「存在」：`WindowChromeSmoke` 算的是**相对亮度方向** ——
> 亮色主题的标题栏必须**亮**、暗色主题的必须**暗**；标题文字与底色要拉得开（Δ>0.35）、
> 边框与标题栏要能分辨（Δ>0.005）。给亮色主题配一个黑标题栏，键全在、断言全绿、肉眼全错。

### 7.2 跳转定义：修掉了原版两条死的打分规则

`DefinitionFinder` 的打分照搬 C++，但移植时发现原版 **`prevWordAt()` 恒返回空串**：

```c
static std::wstring prevWordAt(int off) {
    int p = off - 1;
    while (p >= 0 && isWordChar(g_text[p])) p--;      // ← 一次都不会执行
    return g_text.substr(p + 1, off - (p + 1));       // ← 恒为空
}
```

`off` 是「整词命中」的起点，它前面那个字符**必然不是词字符**（否则整词判定就不成立），
所以循环体永远进不去，返回的永远是空串。后果是「前置声明关键字 +5」与
「前置首字母大写词 +2」两条规则**在 C++ 版里是死代码**，打分退化成
「只看后面跟不跟 `(`」，于是 `public int Calc(` 与调用处 `Calc(` 几乎同分（4 : 4），
跳转质量全靠「当前行 −1」那点权重和扫描顺序碰运气。

本工程先跳过空白再取词：

```csharp
int p = off - 1;
while (p >= 0 && char.IsWhiteSpace(text[p])) p--;     // ← 先跳空白
int end = p + 1;
while (p >= 0 && IsWordChar(text[p])) p--;
return text.Substring(p + 1, end - (p + 1));
```

于是定义处 1+3+5 = **9** 分、调用处 1+3 = **4** 分，区分度才立得住。
`DefinitionSmoke` 里有一条断言专门守住它：两处调用 + 一处定义，「后跟 `(`」的条件完全一样，
若退回原写法，定义处会掉到与第二处调用同分（4 : 4），而取优用的是严格大于 ——
于是会选中前面那处调用，断言立刻红。

### 7.3 桌面集成三件套：哪一步真的跨不过去

原版这三件事全都建立在「同一个桌面会话里，我能看见别人的窗口」之上。
Windows 给这个能力，Linux/Wayland **在协议层面就不给**，macOS 也没有公开 API。
所以不是「实现方式要换」，而是其中一部分**功能本身要缩水**。逐条说清楚缩在哪：

**① 单实例 + 文件转发 —— 完整实现，但机制换了。**

原版是 `CreateMutexW` 判重 → `FindWindowW` 找到那个窗口 → `WM_COPYDATA` 递路径。
后两步都依赖窗口系统，Wayland 下直接废掉。换成：

```
命名互斥体 LiteReader.SingleInstance.v1   →  判「谁先来的」
命名管道   LiteReader.SingleInstance.v1   →  递「要打开的文件」
```

管道不依赖窗口系统，三平台语义一致（Windows 是内核对象，Unix 上 .NET 落到 `$TMPDIR/.dotnet/corefx/`
下的本地 socket 文件 —— 天然同用户可见、跨用户隔离，正是单实例要的语义）。
**空列表 = 只把已有窗口叫到前面来**，所以「双击图标」这条路径也走同一个通道。
本轮已做真机双进程验证（§5.6）。

> 唯一缩水处：**「叫到前面来」在 Wayland 上不保证生效** ——
> 合成器决定要不要允许客户端自己提升窗口，通常只在「用户刚点了图标」这种有输入事件时才放行。
> 能做的都做了（`WindowState` 还原 + `Activate()`），但不承诺。

**② 文件关联 —— Windows 与原版逐字一致，另两平台按各自规矩来。**

沿用原版那套 HKCU 注册（`Applications\LiteReader.exe` + 每个扩展名的 `OpenWithList`），
**刻意不做的一件事**：不去写 `Software\Classes\<扩展名>` 的默认值。原版也没做。
抢默认关联会顶掉用户机器上 VS Code / Notepad++ 的设置，装个阅读器不该有这种后果 ——
`FileAssociationSmoke` 里专门有一条断言钉住这个决定。

> ⚠️ **会覆盖旧版的注册**：C++ 单文件版用的**是同一个键名**
> （`HKCU\Software\Classes\Applications\LiteReader.exe`），因为两者的可执行文件都叫 `LiteReader.exe`。
> 所以「注册为打开方式」会把旧版那条顶掉。这本身合理 —— 同一个程序只该有一个条目 ——
> 但**默默改掉用户的关联是最讨人厌的那种「我帮你优化了」**，所以注册报告里会打印出被覆盖的原值，
> 想还原就把它写回去（或跑一次旧版的「注册」菜单）。

| 平台 | 做法 |
|---|---|
| Windows | HKCU 注册表（**不需要管理员**，因为写 HKCU 不是 HKCR） |
| Linux | 写 `~/.local/share/applications/lite-reader.desktop`，再尽力跑 `xdg-mime default` + `update-desktop-database`（没装 xdg-mime 时只写文件，也算成功） |
| macOS | **做不到，也不硬做**：文件类型登记在 `.app` 包的 `Contents/Info.plist` 里，而运行中的 .app 一般已签名，改它等于破坏签名。所以只生成一段可粘贴的 plist 片段 + 完整步骤 |

结果用**标签页**显示（写成一个文本文件再打开），不弹原生对话框 ——
macOS 那份报告是一整段要人自己粘的 plist 加四条命令，状态栏装不下，
而在自绘编辑器里弹系统警告框的观感一直很割裂。用标签页显示纯文本，恰好是这个程序最擅长的事。

**③ 终端集成 —— 能开，但不再复用已开的那个窗口。**

「先试 `wt.exe`，再试 `powershell.exe`，最后 `cmd.exe`」这个**有序尝试的形状**是跨平台的，
换掉的只是候选清单（跨平台能力矩阵见 README §9.1）。原版还有一步 `EnumWindows` 找已开的终端窗口把它叫到前面来 ——
这一步在 Wayland/macOS 都做不到，硬写就是三份平台代码里必有一份是废的，
所以**直接砍掉**：只承诺「能开一个 cd 到目标目录的终端」，不承诺复用。
目录优先级也从原版的「当前文件目录 → 我的文档」改成
「当前文件目录 → 上次打开的文件夹 → 主目录」—— 中间那级换成上次文件夹比直接跳主目录更有用。

**④ Ctrl+滚轮调字号 —— 一件纯粹移植、零缩水的事。**

对照原版 `WM_MOUSEWHEEL` 分支里的 `GetKeyState(VK_CONTROL)`：每格 ±1 pt，先夹取再重排。
本工程把这段抽成 `EditorSurface.HandleWheel(deltaY, mods)`，形状与 `HandleKey`/`HandleText` 一样 ——
**为了能自检**（`PointerWheelEventArgs` 没法伪造，得先凑一个 `IPointer` 实现）。

两个与原版的差别，都是有意为之：

- **范围**：原版夹在 9~28，这里用 `EditorSettings.Min/MaxFontSize` = **8~32**。
  字号在本工程还要伺候高 DPI 屏和投影仪，两端各多留一点。
- **优先级**：`Ctrl` 分支必须排在 `Shift` 分支**之前**。
  `Ctrl+Shift+滚轮` 会同时满足两个条件，而「按住 Ctrl 想调字号」是明确意图，不该被 Shift 抢去横向滚动。
  这条有专门断言（`Ctrl+Shift+滚轮 归 Ctrl`）。

顺带接了一个容易漏的线：字号真值在 `EditorSettings`，状态栏读的是 VM 上的镜像。
Ctrl+滚轮**不经过 VM 的命令**，所以 VM 必须订阅 `EditorSettings.Changed`，否则状态栏会一直停在旧值 ——
`WheelSmoke` 里那条「VM 的字号镜像同步」就是钉这个的。

### 7.4 双击分词高亮 / 滑动导航条 / 右键菜单

这三样都是原版「一用就回不去」的日常能力。逐项说清楚哪一步必须怎么做、哪一步缩了水。

**① 双击分词高亮（对照 `collectMarks` + `markBg()`）**

三件事**做错了就静默失效**：

- **只收整词命中** —— 命中处前一字符与后一字符都不能是单词字符。
  否则双击 `in` 会把 `index` / `inline` 里面的 in 一起点亮；在大文件里这不是「多亮几个字」，
  是一屏糊满底色。
- **SQL 的 `BEGIN` / `END` / `CASE` 是例外**：不做全文档高亮，只选中该词本身。
  一个几百行的存储过程里有几十个 `END`，全点亮等于把正文涂满。判定**大小写敏感**
  （`w == L"END"`），所以 C# 里的小写 `case` 不受影响。
- **按行分桶**（`Dictionary<int, List<MarkRange>>`），而不是平铺一张区间表。
  大文件下双击一个常见词能有几万处命中，每帧遍历全表 + 逐条二分查找会掉帧；
  分桶之后 Render 只查当前可视的那几十行。

清除判据值得单独说：**不是 `DocumentModel.Version`，而是「`Document.Text` 这个字符串实例
还是不是当初收集标记时的那一份」**。因为 `Save()` 与 `Language` 赋值**都会抬 `Version`
并发 `Changed`，但文本一个字没动** —— 按 Version 判的话，按一次 `Ctrl+S` 就把高亮抹掉了。
（自检里 `编辑文本后分词高亮自动清除` 与 `单击正文清掉分词高亮` 两条钉的就是这个行为。）

| 落点 | 行为 |
|---|---|
| 落在单词字符上 | 取整个词（向左向右都扩展） |
| 落在空白 / 标点上 | **退化成选相邻一个字符，且不点亮任何词** |
| 双击的是 SQL 块括号词（`BEGIN`/`END`/`CASE`） | 只选中它，不做全文高亮 |

> 第二行是有意**偏离原版**的：原版此时什么都不选，用户会以为「双击没反应」。
> 第三行的例外源于本项目自己的 `LanguageMode`（原版也有 SQL 识别，只是没做这条例外）。
>
> 落点算法沿用原版的 `wordAtOffset` 语义：**只有落在单词字符上才向右扩展，向左无条件扩展**
> （原版为「接受补全时吞掉换行符」打的补丁），有专门断言守住。

**缩水处**：原版把标记存成一张全局区间表逐帧扫；本工程按行分桶解决了**渲染**的开销，
但几万处命中的**内存**照样要占（每处一个 record struct，8 字节）。
样本量级实测没问题，但没有上限保护 —— 真要在百 MB 文件里双击一个 `a` 之类的词，
应该加个「命中超过 N 处就不点亮」的闸门。

**② 右侧滑动导航条（原版用系统 `WS_VSCROLL`）**

原版这一条是白送的：`SetScrollPos` + 处理 `WM_VSCROLL` 的 `SB_THUMBTRACK`，
滚动条的外观与交互全归 Windows。自绘之后这三处几何必须自己算对：

- **滑块高度 = 轨道高 × (可视行数 / 总行数)**，并**必须夹一个最小高度**
  （`OverviewMinThumb = 28`）。10 万行的文件 + 可视 40 行 → 算出来滑块只有 **0.3 px**，
  既看不见也拖不住。
- **滑块位置的分母是 `totalLines - visibleLines`，不是 `totalLines - 1`**。
  走到最后一行时滑块该正好贴底；用后者永远差一点，视觉上表现为「底下留一条空档」。
- **`LineFromThumbTop` 与 `ThumbTop` 必须互为逆运算**（有往返断言钉住：450 → 450）。
  两个方向各自取整的话，拖动时会出现「手一松就跳一格」。

计算全部收在 `Models/OverviewBar.cs` 里、都是纯函数，所以可以不开窗口测边界值
（`OverviewSmoke` 14 条，含「轨道高 ≤ 滑块高时不除以零」「超出轨道被夹住」这类）。

**缩水处**：系统滚动条**自带键盘 / 滚轮 / 点轨道翻页 / 右键菜单跳转**这些行为，
本工程只实现了「按住拖 + 点击定位」。刻意不做的原因是成本收益比 ——
自绘要还原这套系统级交互（拖拽提示条、双击滚动条跳到点击位置、长按连续翻页）
工作量远大于收益，而滚轮与 `Ctrl+Home/End` 本来就有。
**另一点不同**：原版滚动条是「整份文档的滚动条」，本工程是「按行的导航条」——
宽度固定 13 px、随光标所在行同步移动，所以它同时也兼任「当前位置指示」。

**③ 右键菜单（对照 `showEditorMenu`）**

项序逐项照抄，段分隔符位置也一样：

```
复制 / 剪切 / 粘贴   →   ─────   →   全选   →   ─────
在命令提示符中打开   →   ─────   →   跳转到定义
```

灰显条件：无选区 → 灰掉复制 / 剪切；剪贴板无文本 → 灰掉粘贴；取不到标识符 → 灰掉跳转到定义。

两处必须自己想清楚的地方：

- **右键时先把光标落到点击处，但落点已在现有选区内则保留选区**。
  否则「选好一段文字 → 右键 → 复制」这一下会把选区点没，菜单里的复制直接是灰的。
  `PlaceCaretForContext` 就是干这个的，有两条断言分别守住「落在选区内」与「落在选区外」。
- **Avalonia 的剪贴板 API 全是异步的**，所以「剪贴板没文本就灰掉粘贴」只能在菜单弹出后
  补一次刷新（`SyncPasteEnabledAsync`）。这里**绝不能 `.Result` / `.Wait()`** ——
  UI 线程上等自己的续体是教科书式死锁，表现是右键菜单一出来整个窗口就卡住。

两条 Avalonia 用法顺带记下来：

- `ContextRequestedEvent` 用 `RoutingStrategies.Tunnel` + `AddHandler` 挂，
  **不设 `Handled`** —— 菜单照常由框架打开，我们只是抢在它前面把光标和可用性准备好。
- `EditorSurface` 的 `DataContext` 是**标签 VM**（`DocumentTabViewModel`）而不是主窗口 VM，
  所以菜单动作的反馈（「没找到定义」「已在命令提示符中打开」）走
  `EditorSurface.StatusReported` 静态事件递出去，由 `MainWindow` 订阅后写状态栏。
  **静态事件必须在 `OnClosing` 里退订**，否则窗口关掉之后事件还挂着一份引用。

**缩水处**：原版的菜单里「粘贴」那一项的灰显是同步判的；本工程因为 API 是异步的，
先按「可用」画出来、拿到剪贴板结果再改，理论上有一帧的观感差异 —— 用不死锁换的，划算。

**④ 顺手补的一处对称**：三击选行原本内联在 `OnPointerPressed` 里，
本轮抽成 `TripleClickLine(offset)`，与新的 `DoubleClickWord(offset)` 对称 ——
两者都是 `internal`，自检直接驱动，与真实鼠标点击走的是同一段逻辑。

### 7.5 窗口外框跟随主题 / 图标资源

对照的是原版 `LiteReader.cpp` 里那几处 `DwmSetWindowAttribute` 调用（把系统标题栏染成主题色）。

#### ① 外框：三件事，互相独立，缺一件就是「对了但看着不对」

| DWM 属性 | 值 | 在哪一代系统可用 | 管什么 |
|---|---|---|---|
| `DWMWA_USE_IMMERSIVE_DARK_MODE` | 20（更早的 build 用 19） | Win10 1809+ | **标题栏那三个按钮字形的明暗** |
| `DWMWA_CAPTION_COLOR` | 35 | **Win11 22000+** | 标题栏底色 |
| `DWMWA_TEXT_COLOR` | 36 | **Win11 22000+** | 标题栏**文字**色 |
| `DWMWA_BORDER_COLOR` | 34 | **Win11 22000+** | 整圈外边框 |

最容易写错的三处：

1. **参数是 `COLORREF`（`0x00BBGGRR`），不是 Avalonia 的 `#AARRGGBB`** —— 直接把
   `#282C34` 塞进去会得到 `#342C28`（红蓝互换）。转换是
   `ToColorRef(c) => (c.B << 16) | (c.G << 8) | c.R`，自检有正反两条断言钉着。
2. **深色标志与那三个颜色是独立的**：前者只影响按钮字形，后者才管颜色。
   亮色主题必须把前者传 **0**，否则会出现「浅色标题栏 + 浅色字形」，肉眼看就是
   「最小化/关闭按钮不见了」。`ChromeSmoke` 逐主题断言 `DarkMode == !theme.IsLight`。
3. **属性值会持久化在窗口上**：`_current` 主题字典换了不会自动重施，必须显式重调。
   所以 `MainWindow` 订阅 `ThemeService.Changed`，换主题就重跑一遍 `ApplyChrome()`。

#### ② 缩水处：macOS / Linux 如实不做，不假装支持

| 平台 | 能做到什么 | 为什么 |
|---|---|---|
| Windows 11 22000+ | **完整**：标题栏底色/文字色/外边框三样都按主题上色 | 有公开的 DWM 属性 |
| Windows 10 | 只支持「深色标志」 | 三个颜色属性是 Win11 才加的，传下去返回失败 |
| macOS | **只能跟随系统深浅色** | 只有 `NSWindow.appearance`，**没有「指定十六进制颜色」的 API** |
| Linux | **交给桌面环境** | X11 各家 WM 各按各的规矩；Wayland 协议层就不给客户端画装饰的机会 |

所以 `WindowChrome.Supported` / `CustomColorSupported` 在非 Windows 上直接返回 false，
`ChromeResult.Detail` 里写明原因；自检**两个方向都断言**：Windows 上必须真的施加成功
（`Applied && AttributesSet >= 1`），非 Windows 上必须 `!Applied` —— 后者守的是
「不要假装支持」（假装成功比不支持更坏，用户会一直找为什么没变色）。

跨平台的做法是**自绘标题栏**（`ExtendClientAreaToDecorationsHint`）。本轮没走这条路，因为
它要把窗口按钮、拖拽区、最大化行为全部自己实现一遍，而收益只在非 Windows 上体现 ——
留给 §9 待做，等真去 Linux/macOS 上验的时候再一起做。

#### ③ 图标：一份图，三种格式，三处用途，必须同源

```
spikes/winshot/iconbuild/icon_gen.cpp   （g++ 编译运行）
        ├── appicon.ico   多尺寸 → csproj 的 <ApplicationIcon> → 进 exe 的 PE 资源段
        └── icon.png      256×256（ico2png.py 从上面那份 ICO 抽）

用途：
  appicon.ico → 资源管理器 / 任务栏 / Alt+Tab（这些读的是 exe 的 PE 资源，不是运行时）
  icon.png    → ① Window.Icon                      （运行时，PNG 解码比 ICO 稳）
                ② Linux ~/.local/share/icons/hicolor/256x256/apps/lite-reader.png
                ③ Linux .desktop 里的 Icon=        （★ 必须是**名字**不是路径）
```

三个坑：

- **`Icon=` 必须是名字**（`Icon=lite-reader`），写路径 xdg 不认；
  而且 `hicolor` 下的目录名 `256x256` **必须与实际像素尺寸一致**，
  否则图标在部分桌面环境下静默不显示。
- **`Window.Icon` 走程序集资源**（`avares://LiteReader/Assets/icon.png`）而不是
  exe 旁边的文件 —— 单文件发布下 exe 旁边**没有** `Assets/` 这个目录。
- **macOS 的 `.icns` 只能给步骤**：`iconutil` 从 `.iconset` 打包 + `sips` 缩放 + 重新签名。
  本工程不内置生成（运行时改已签名的 `.app` 等于破坏签名），所以在
  `FileAssociation` 的 macOS 报告里打印完整步骤让用户自己跑。

#### ④ 验非客户区：只能抓真实窗口

`--selftest` 的截图走 `RenderTargetBitmap`，**只抓客户区**。标题栏、外边框、标题栏小图标
全在非客户区 —— 而那恰好是本功能唯一的验收点。所以另有一套 `spikes/winshot/`（见 §5.3 ⑤）。

两个实测教训：

- **`GetWindowRect` 在 Win11 上包含约 8px 不可见缩放边框**（实测
  `visible=(2452,91,3734,943)` vs `GetWindowRect=(2445,91,3741,950)`）。
  按前者抓图会多一圈桌面背景，而「外边框颜色」的采样点会落进客户区，
  量出来是正文色 —— 表现为「三条边框全部 FAIL，左边框量到桌面壁纸色」。
  必须用 `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS=9, ...)`。
- **采样要扫描，不要写死坐标**：`probe_shell.py` 用 run-length 扫出条带边界，
  布局一变仍能量对地方；写死坐标会在布局微调后**静默**量错位置 ——
  而这份脚本的价值恰恰是「不骗人」。

本机实测（One Light，`--margin 0` 抓图 + `probe_chrome.py`）：

```
标题栏 主色 #F0F0F1 → 期望 #F0F0F1  偏差 0  PASS
左边框 #D8D8DA → 偏差 0 PASS   右边框 #D8D8DA → 偏差 0 PASS   下边框 #D8D8DA → 偏差 0 PASS
标题文字 #383A42：命中                PASS
窗口图标：命中                        PASS
```

5 套主题的**外壳条带**（`probe_shell.py` 扫主题截图）：

```
主题         状态栏     左侧栏     分隔条      结论
One Dark Pro #21252B   #282C34   #21252B    PASS
One Light    #F0F0F1   #FAFAFA   #F0F0F1    PASS
VS Code Dark #1E1E1E   #1E1E1E   （同色）    PASS
IntelliJ IDEA #313335  #2B2B2B   #313335    PASS
极致黑 AMOLED  #0A0A0A   #000000   #0A0A0A    PASS
```

**留痕**：`selftest/chrome/` —— `window-one-light.png`（真实窗口全图，One Light 下
「浅色外框 + 浅色内容」的一致观感就靠它看）、`chrome-pixels.txt`、`shell-pixels.txt`。
这两个 `.txt` 是上面两张表的原始读数（**证据要是可复查的文本，不是一句「已验证」**）。

> 顺带记一条**属性可信度**的教训：`TabControl.Background` 在 5 套主题下恒为 `#FFFFFF`，
> 但它的模板根本不绘制这个属性（真实截图里标签栏画的就是主题底色）。
> 所以「读属性」与「看像素」是两把不同的尺子，**能读属性就断言属性，属性不参与绘制时才回到像素**，
> 别把两者混起来下结论。

---

## 9. 待办

### 已完成

- [x] **1. 编辑区做成可编辑** —— 光标/选区/插入删除 + 操作式撤销栈（对照 `LiteReader.cpp` 的撤销设计）
- [x] **2. 查找、跳转定义、代码补全移植** —— 外加**彩虹括号 + 括号配对**
      （这一项此前不在清单里，是漏写的核心能力，见 §7.1）
  - 彩虹括号：`()[]{}` 按嵌套深度循环 6 色，**深度跨行**（与块注释共用 `LineState` 懒推进）；
    SQL 的 `BEGIN/END/CASE` 当词括号
  - 括号匹配：光标处的括号高亮配对（同类型配对，鼠标点击也算）
  - 查找：顶部查找条 + `Ctrl+F` / `F3` / `Shift+F3`，循环查找、`Aa` 大小写、增量查找
  - 跳转定义：`F12` / `Ctrl+单击`，单文件启发式打分（并修掉了原版两条死的打分规则，见 §7.2）
  - 代码补全：自绘候选面板（文档内函数名 + 按语言的关键字表）+ 括号/引号自动配对
  - 全部 305 条断言在 `--selftest` 里，其中 64 条是这些能力（含双击分词/导航条/右键菜单三项）在**真实控件**上的闭环
- [x] **3. 配置持久化**（XDG 目录 + JSON）—— 含窗口几何、上次文件夹、主题、字号、补全开关、查找大小写
- [x] **4. 文件关联、单实例、终端集成**（平台差异全部收在 `Services/` 下，设计说明见 §7.3）
  - **单实例 + 文件转发**：`SingleInstance` —— 命名互斥体判重 + 命名管道转发。
    之所以不照搬原版的 `FindWindow` + `WM_COPYDATA`：Wayland 下**协议层面就不允许**枚举窗口。
    空列表 = 只把已有窗口叫到前面来。已做真机双进程验证（§5.6，含「带文件」与「不带文件」两条路径）
  - **文件关联**：`FileAssociation` —— Win 沿用原版的 HKCU 注册表（`Applications` + `OpenWithList`），
    Linux 写 `.desktop` + 尽力跑 `xdg-mime`，macOS 只生成 plist 片段与步骤（运行中的 .app 已签名，改不得）。
    **只进「打开方式」列表，不抢默认关联**（有断言钉住）
  - **终端集成**：`TerminalLauncher` —— 沿用原版「有序尝试」的形状，
    候选清单三平台各一份（Win `wt.exe`→`powershell`→`cmd` 与原版同序）。
    原版那个「找已开的终端窗口并叫到前面」的一步**砍掉了**：Wayland/macOS 做不到
  - 全部平台判断都在 `Services/` 里，VM 与 XAML 里一句都没有
- [x] **4b. Ctrl+鼠标滚轮调字号**（原版有的，之前漏了）
  - `EditorSurface.HandleWheel(deltaY, mods)`，每格 ±1 pt，夹在 `EditorSettings.Min/MaxFontSize`（8~32）
  - 优先级：Ctrl 分支排在 Shift 之前，`Ctrl+Shift+滚轮` 归调字号
  - 顺带补上「字号的 VM 镜像要订阅 `EditorSettings.Changed`」这条漏掉的接线
- [x] **4c. 双击分词高亮 / 右侧滑动导航条 / 右键菜单**（原版有，本轮补齐；设计说明见 §7.4）
  - **双击分词高亮**：`WordMarker` —— 只收整词命中（双击 `in` 不会点亮 `index`），
    SQL 的 `BEGIN`/`END`/`CASE` 是例外（只选中不全文高亮），命中**按行分桶**；
    清除判据用「文本字符串实例」而不是 `Version`（否则按一次 `Ctrl+S` 就把高亮抹掉）
  - **右侧滑动导航条**：`OverviewBar` —— 滑块高度 = 可视占比（夹最小高度，否则十万行时只有 0.3 px）、
    位置分母用 `totalLines - visibleLines`（走到最后一行才正好贴底）、位置 ↔ 行号互为逆运算。
    **缩水处**：只做「按住拖 + 点击定位」，不还原系统滚动条的键盘/点轨道翻页等系统级交互
  - **右键菜单**：项序与原版 `showEditorMenu` 逐项一致；右键落在选区内会**保留选区**
    （否则「选好文字 → 右键 → 复制」会发现自己被点没了）；剪贴板是异步 API，
    所以粘贴的灰显只能弹后补刷一次（**绝不能 `.Result`/`.Wait()`**，UI 线程上等自己必死锁）
  - 三击选行顺带抽成 `TripleClickLine(offset)`，与新的 `DoubleClickWord(offset)` 对称
  - 新增自检：纯逻辑 `WordMarkerSmoke` 17 条 + `OverviewSmoke` 14 条；
    真实控件闭环 28 条（双击分词 10 / 导航条 10 / 右键菜单 8）；
    主题键从 12 个扩到 15 个（`EditorMarkBackground` / `EditorOverviewBackground` / `EditorOverviewThumb`）
- [x] **4d. 窗口外框跟随主题 + 图标资源**（本轮；设计说明与缩水处见 §7.5）
  - **外框**：`Services/WindowChrome.cs` —— DWM 四个属性（深色标志 + 标题栏底色/文字色/外边框），
    参数是 `COLORREF`（`0x00BBGGRR`，**反着读**）；`MainWindow.ApplyChrome` 在 `Opened`
    与 `ThemeService.Changed` 两处施加（属性值会持久化在窗口上，换主题必须显式重施）
  - **缩水处**：Win11 22000+ 三样都有；Win10 只剩「深色标志」；
    **macOS / Linux 如实不做** —— macOS 只有 `NSWindow.appearance`（跟随系统深浅色，
    没有指定十六进制颜色的 API），Wayland 协议层就不给客户端画装饰的机会
  - **图标**：`icon_gen.cpp` 生成**同源**的 `appicon.ico`（→ `ApplicationIcon`，进 PE 资源段）
    与 `icon.png`（→ `Window.Icon` 走程序集资源 + Linux `hicolor/256x256/apps/`）；
    Linux 的 `.desktop` 里 `Icon=` 写**名字**不写路径；macOS `.icns` 只给步骤（运行时改已签名的 .app 等于破坏签名）
  - **顺手挖掉一个既有 bug**：`App.axaml` 的默认色**遮蔽**了合并字典里的主题值，
    导致「编辑器正文跟着主题变、外壳不跟」—— 详见 §5.3 ⑤ 与 §7.5 ①
  - 新增自检：纯逻辑 `WindowChromeSmoke` 22 条 + `IconSmoke` 5 条；
    真实控件 `ChromeSmoke` 10 条 + `ShellThemeSmoke` 26 条
  - 主题键从 15 个扩到 **18** 个（`WindowCaptionBackground` / `WindowCaptionForeground` / `WindowBorder`）
- [x] **5. 主题改静态引用，消除 `IL2026` 裁剪隐患** —— 现为 0 警告 0 错误
- [x] **6. 跨行块注释**（文档级状态机）—— 含跨行字符串续行，已进 `--selftest` 与 `--fuzz` 断言

### 待做

7. **大文件（>100 MB）分段加载 / 内存映射** —— 当前是整串读入 `string`
8. **CI 三平台各自冒烟** —— 托管发布可单机交叉发布，但
   `--selftest` 必须在三个平台上各自跑；`--fuzz` / `--bench-edit` 不需要窗口，
   可以直接放进任意平台的流水线
   - 顺带：`--selftest` 里的「单实例真机双进程」那一段（§5.6）目前是手工跑的，
     可以脚本化后一起进 CI（注意它要真起进程，不能在无显示环境的容器里跑）
9. **Linux / macOS 真机验证** —— 目前只在 Windows 上跑过
   - 本轮的桌面集成三件套尤其需要真机：`xdg-mime` 那两步、`.desktop` 里的 `%F` 展开、
     macOS 的 plist 步骤，在 Windows 上都只是「结构断言通过」
   - 单实例在两个 Linux 桌面（X11 / Wayland）上的表现要分别看：管道机制本身与显示服务器无关，
     但「叫窗口到前面」在 Wayland 上不保证生效
   - 本轮的导航条与右键菜单也要过一遍：`ContextMenu` 在 macOS 上的弹出位置、
     以及自绘导航条在两倍缩放屏上的宽度是否需要跟着 DPI 走
   - 外框那一项也要复验两件事：**Windows 10 上的缩水路径**（只有深色标志时观感如何）、
     以及 Linux/macOS 上 `WindowChrome.Applied == false` 时**不该有任何视觉副作用**
     （自检已断言「非 Windows 不假装成功」，但真机观感没人看过）
10. **拖放打开文件**（README §9.1 平台能力矩阵里剩下的最后一行 ❌）—— Avalonia `DragDrop`（跨平台，比原版的 `WM_DROPFILES` 干净）
    - 原来的「图标资源 + 暗色标题栏」已在本轮完成（§7.5）；剩下的**跨平台自绘标题栏**
      （`ExtendClientAreaToDecorationsHint`）也一并放这里 —— 它要把窗口按钮、拖拽区、
      最大化行为全部自己实现一遍，收益只在非 Windows 上体现，等去真机验证时再一起做
11. **双击分词高亮的命中上限**（见 §7.4 ①）
    - 现在命中几万处会照单全收（每处 8 字节）。理论上该加个闸门：
      命中数超过阈值就退化成「只高亮可视行」或直接不点亮，避免百 MB 文件上双击常见词时的内存尖峰
12. **`WindowChrome` 的重施时机补一处 `Activated`**（见 §7.5 ①）
    - 目前只在 `Opened` 与 `ThemeService.Changed` 上施加。若用户在系统里关掉了
      「动画效果」（`SPI_GETCLIENTAREAANIMATION`），DWM 有可能忽略捕获窗口的颜色请求；
      在 `Activated` 上再施一次是低成本的兜底。属稳妥性加强，不是已证实的缺陷
