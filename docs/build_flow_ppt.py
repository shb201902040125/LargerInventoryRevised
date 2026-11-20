# -*- coding: utf-8 -*-
"""Build the LargerInventory code-execution-flow deck (21 slides, Chinese)."""

import os
from lxml import etree
from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.enum.shapes import MSO_SHAPE, MSO_CONNECTOR
from pptx.enum.dml import MSO_LINE_DASH_STYLE as DASH
from pptx.oxml.ns import qn

# ---------------------------------------------------------------- palette ----
INK = RGBColor(0x0F, 0x17, 0x2A)
BODY = RGBColor(0x33, 0x41, 0x55)
MUT = RGBColor(0x64, 0x74, 0x8B)
FAINT = RGBColor(0x94, 0xA3, 0xB8)
LINE = RGBColor(0xE2, 0xE8, 0xF0)
CARD = RGBColor(0xF8, 0xFA, 0xFC)
CARD2 = RGBColor(0xF1, 0xF5, 0xF9)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)

BLUE = RGBColor(0x25, 0x63, 0xEB)
BLUE_L = RGBColor(0xDB, 0xEA, 0xFE)
BLUE_D = RGBColor(0x1E, 0x40, 0xAF)
TEAL = RGBColor(0x0D, 0x94, 0x88)
TEAL_L = RGBColor(0xCC, 0xFB, 0xF1)
AMBER = RGBColor(0xD9, 0x77, 0x06)
AMBER_T = RGBColor(0xB4, 0x53, 0x09)
AMBER_L = RGBColor(0xFE, 0xF3, 0xC7)
PURPLE = RGBColor(0x7C, 0x3A, 0xED)
PURPLE_L = RGBColor(0xED, 0xE9, 0xFE)
RED = RGBColor(0xDC, 0x26, 0x26)
RED_L = RGBColor(0xFE, 0xE2, 0xE2)
GREEN = RGBColor(0x16, 0xA3, 0x4A)
GREEN_L = RGBColor(0xDC, 0xFC, 0xE7)
SLATE = RGBColor(0x47, 0x55, 0x69)

NAVY = RGBColor(0x0B, 0x12, 0x20)
NAVY2 = RGBColor(0x16, 0x21, 0x3A)
NAVY_LINE = RGBColor(0x33, 0x41, 0x55)

FONT = "Microsoft YaHei"
MONO = "Consolas"

W, H = 13.333, 7.5
TOTAL = 21

prs = Presentation()
prs.slide_width = Inches(W)
prs.slide_height = Inches(H)
BLANK = prs.slide_layouts[6]


# ---------------------------------------------------------------- helpers ----
def set_run(run, size, color, bold=False, font=FONT, italic=False, spc=None):
    f = run.font
    f.size = Pt(size)
    f.bold = bold
    f.italic = italic
    f.color.rgb = color
    f.name = font
    rPr = run._r.get_or_add_rPr()
    ea = rPr.find(qn("a:ea"))
    if ea is None:
        ea = etree.SubElement(rPr, qn("a:ea"))
    ea.set("typeface", font)
    if spc is not None:
        rPr.set("spc", str(spc))


def write_paras(tf, paras, anchor=MSO_ANCHOR.TOP, margins=(0.06, 0.06, 0.04, 0.04), wrap=True):
    tf.word_wrap = wrap
    tf.margin_left = Inches(margins[0])
    tf.margin_right = Inches(margins[1])
    tf.margin_top = Inches(margins[2])
    tf.margin_bottom = Inches(margins[3])
    tf.vertical_anchor = anchor
    first = True
    for para in paras:
        p = tf.paragraphs[0] if first else tf.add_paragraph()
        first = False
        p.alignment = para.get("align", PP_ALIGN.LEFT)
        if para.get("space_before") is not None:
            p.space_before = Pt(para["space_before"])
        if para.get("space_after") is not None:
            p.space_after = Pt(para["space_after"])
        if para.get("line") is not None:
            p.line_spacing = para["line"]
        runs = para.get("runs")
        if runs is None:
            runs = [(para.get("t", ""), {})]
        for text, style in runs:
            if text == "":
                continue
            r = p.add_run()
            r.text = text
            set_run(
                r,
                style.get("size", para.get("size", 10)),
                style.get("color", para.get("color", BODY)),
                bold=style.get("bold", para.get("bold", False)),
                font=style.get("font", para.get("font", FONT)),
                italic=style.get("italic", False),
                spc=style.get("spc", para.get("spc")),
            )


def txt(slide, x, y, w, h, paras, anchor=MSO_ANCHOR.TOP, align=PP_ALIGN.LEFT,
        margins=(0.02, 0.02, 0.02, 0.02), wrap=True):
    tb = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    write_paras(tb.text_frame, paras, anchor=anchor, margins=margins, wrap=wrap)
    return tb


def panel(slide, x, y, w, h, fill=None, line=None, lw=0.9, radius=0.08,
          shape=MSO_SHAPE.ROUNDED_RECTANGLE, dash=None):
    s = slide.shapes.add_shape(shape, Inches(x), Inches(y), Inches(w), Inches(h))
    if shape == MSO_SHAPE.ROUNDED_RECTANGLE:
        s.adjustments[0] = radius
    sp = s._element
    st = sp.find(qn("p:style"))
    if st is not None:
        sp.remove(st)
    if fill is None:
        s.fill.background()
    else:
        s.fill.solid()
        s.fill.fore_color.rgb = fill
    if line is None:
        s.line.fill.background()
    else:
        s.line.color.rgb = line
        s.line.width = Pt(lw)
        if dash is not None:
            s.line.dash_style = dash
    s.text_frame.word_wrap = True
    return s


def ptext(shape, paras, anchor=MSO_ANCHOR.MIDDLE, margins=(0.05, 0.05, 0.03, 0.03), wrap=True):
    write_paras(shape.text_frame, paras, anchor=anchor, margins=margins, wrap=wrap)


def arrow(slide, x1, y1, x2, y2, color=MUT, w=1.4, dash=None, head=True):
    c = slide.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, Inches(x1), Inches(y1),
                                   Inches(x2), Inches(y2))
    c.line.color.rgb = color
    c.line.width = Pt(w)
    if dash is not None:
        c.line.dash_style = dash
    if head:
        ln = c.line._get_or_add_ln()
        tail = ln.find(qn("a:tailEnd"))
        if tail is None:
            tail = etree.SubElement(ln, qn("a:tailEnd"))
        tail.set("type", "triangle")
        tail.set("w", "med")
        tail.set("len", "med")
    return c


def hline(slide, x1, y, x2, color=LINE, w=1.0, dash=None):
    return arrow(slide, x1, y, x2, y, color=color, w=w, dash=dash, head=False)


def vline(slide, x, y1, y2, color=LINE, w=1.0, dash=None, head=False):
    return arrow(slide, x, y1, x, y2, color=color, w=w, dash=dash, head=head)


def chip(slide, x, y, w, h, text, fill, color, size=9.0, bold=True, font=FONT,
         line=None, lw=0.8, radius=0.5, align=PP_ALIGN.CENTER):
    p = panel(slide, x, y, w, h, fill=fill, line=line, lw=lw, radius=radius)
    ptext(p, [{"t": text, "size": size, "color": color, "bold": bold, "font": font,
               "align": align}], anchor=MSO_ANCHOR.MIDDLE,
          margins=(0.05, 0.05, 0.01, 0.01), wrap=False)
    return p


def est_w(text, size, mono=False):
    w = 0.0
    for ch in text:
        if ord(ch) > 0x2E80:
            w += 1.0
        elif mono:
            w += 0.60
        else:
            w += 0.52
    return w * size / 72.0


def auto_chips(s, x, y, h, texts, col, right=12.6, start_size=8.6, gap=0.12, max_w=2.9):
    """Lay out pills from x to right, shrinking the font until the row fits."""
    size = start_size
    for size in (start_size, start_size - 0.3, start_size - 0.6, start_size - 0.9, start_size - 1.2):
        ws = [min(est_w(t, size) + 0.34, max_w) for t in texts]
        if x + sum(ws) + gap * (len(ws) - 1) <= right:
            break
    xx = x
    for t, w in zip(texts, ws):
        chip(s, xx, y, w, h, t, WHITE, BODY, size=size, bold=False, line=col, lw=0.6)
        xx += w + gap


def slide_base(kicker, title, subtitle, color):
    s = prs.slides.add_slide(BLANK)
    panel(s, 0, 0, W, 0.075, fill=color, shape=MSO_SHAPE.RECTANGLE)
    txt(s, 0.6, 0.24, 10.5, 0.28,
        [{"t": kicker, "size": 9.0, "color": color, "bold": True, "spc": 80}])
    txt(s, 0.6, 0.50, 12.13, 0.62,
        [{"t": title, "size": 23, "color": INK, "bold": True, "line": 1.0}])
    if subtitle:
        txt(s, 0.6, 1.13, 12.13, 0.36,
            [{"t": subtitle, "size": 11.0, "color": MUT, "line": 1.0}])
    return s


def footer(s, n):
    hline(s, 0.6, 7.16, 12.73, color=LINE, w=0.9)
    panel(s, 0.6, 7.24, 0.09, 0.09, fill=FAINT, shape=MSO_SHAPE.RECTANGLE)
    txt(s, 0.76, 7.20, 6.0, 0.22,
        [{"t": "LargerInventory v0.7.0 · 代码运行流程", "size": 8.0, "color": FAINT}])
    txt(s, 10.6, 7.20, 2.13, 0.22,
        [{"t": f"{n:02d} / {TOTAL}", "size": 8.0, "color": FAINT, "align": PP_ALIGN.RIGHT}])


def notes(s, text):
    s.notes_slide.notes_text_frame.text = text


def new_slide(kicker, title, subtitle, color, n, note):
    s = slide_base(kicker, title, subtitle, color)
    footer(s, n)
    notes(s, note)
    return s


def flow_row(s, x, y, w, h, items, gap=0.22, arrow_color=FAINT, bw=None):
    n = len(items)
    if bw is None:
        bw = (w - gap * (n - 1)) / n
    for i, it in enumerate(items):
        bx = x + i * (bw + gap)
        p = panel(s, bx, y, bw, h, fill=it.get("fill", CARD), line=it.get("line", LINE), radius=0.1)
        paras = []
        if it.get("kicker"):
            paras.append({"t": it["kicker"], "size": 8.0, "color": it.get("kicker_color", MUT),
                          "bold": True, "align": PP_ALIGN.CENTER, "space_after": 1})
        paras.append({"t": it.get("title", ""), "size": it.get("tsize", 10.5),
                      "color": it.get("color", INK), "bold": True,
                      "align": PP_ALIGN.CENTER, "line": 1.0})
        if it.get("sub"):
            paras.append({"t": it["sub"], "size": it.get("ssize", 8.2),
                          "color": it.get("scolor", MUT), "align": PP_ALIGN.CENTER,
                          "space_before": 1, "line": 1.0})
        ptext(p, paras, anchor=MSO_ANCHOR.MIDDLE, margins=(0.05, 0.05, 0.03, 0.03))
        if i < n - 1:
            arrow(s, bx + bw + 0.03, y + h / 2, bx + bw + gap - 0.03, y + h / 2, color=arrow_color)
    return bw


def simple_table(s, x, y, w, col_ws, rows, row_h=0.52, header_fill=INK,
                 header_color=WHITE, body_size=8.8, header_size=9.2,
                 zebra=(WHITE, CARD), first_col_bold=True, pad=0.08):
    yy = y
    for ri, row in enumerate(rows):
        is_head = ri == 0
        fill = header_fill if is_head else zebra[(ri - 1) % 2]
        panel(s, x, yy, w, row_h, fill=fill, line=LINE if not is_head else None,
              lw=0.7, radius=0.0, shape=MSO_SHAPE.RECTANGLE)
        cx = x
        for ci, cell in enumerate(row):
            cw = col_ws[ci]
            txt(s, cx + pad, yy, cw - pad * 2, row_h,
                [{"t": cell, "size": header_size if is_head else body_size,
                  "color": header_color if is_head else (INK if ci == 0 and first_col_bold else BODY),
                  "bold": is_head or (ci == 0 and first_col_bold),
                  "line": 1.0}],
                anchor=MSO_ANCHOR.MIDDLE, margins=(0.02, 0.02, 0.02, 0.02))
            cx += cw
        yy += row_h
    return yy


# ================================================================ S1 cover ===
def slide_cover():
    s = prs.slides.add_slide(BLANK)
    panel(s, 0, 0, W, H, fill=NAVY, shape=MSO_SHAPE.RECTANGLE)
    # decorative rings
    for d, col in ((4.5, RGBColor(0x1E, 0x2E, 0x4E)), (5.9, RGBColor(0x17, 0x24, 0x3D))):
        o = s.shapes.add_shape(MSO_SHAPE.OVAL, Inches(11.4 - d / 2), Inches(4.15 - d / 2),
                               Inches(d), Inches(d))
        sp = o._element
        st = sp.find(qn("p:style"))
        if st is not None:
            sp.remove(st)
        o.fill.background()
        o.line.color.rgb = col
        o.line.width = Pt(1.0)

    txt(s, 0.9, 1.12, 7.2, 0.3,
        [{"t": "TERRARIA · tMODLOADER · 源码导读", "size": 10.0,
          "color": RGBColor(0x7D, 0xD3, 0xFC), "bold": True, "spc": 100}])
    txt(s, 0.9, 1.55, 7.6, 0.95,
        [{"t": "LargerInventory", "size": 46, "color": WHITE, "bold": True, "line": 0.95}])
    txt(s, 0.9, 2.52, 7.4, 0.7,
        [{"t": "代码运行流程全景", "size": 28, "color": RGBColor(0x93, 0xC5, 0xFD),
          "bold": True, "line": 1.0}])
    hline(s, 0.92, 3.34, 6.95, color=RGBColor(0x1E, 0x3A, 0x5F), w=1.0)
    txt(s, 0.9, 3.52, 7.0, 1.15,
        [{"t": "从整体架构到局部调用链：模组加载、每帧主循环、指令执行、",
          "size": 13, "color": RGBColor(0xCB, 0xD5, 0xE1), "line": 1.55},
         {"t": "页文件提交、联机交付与页修复。", "size": 13,
          "color": RGBColor(0xCB, 0xD5, 0xE1), "line": 1.55}])

    meta = ["tModLoader 1.4.4", "v0.7.0", "35 个模组源文件"]
    xx = 0.9
    for m in meta:
        w = est_w(m, 9.5) + 0.42
        chip(s, xx, 5.12, w, 0.4, m, NAVY2, RGBColor(0xC7, 0xD2, 0xFE), size=9.5, line=NAVY_LINE)
        xx += w + 0.16

    txt(s, 0.9, 6.42, 8.0, 0.3,
        [{"t": "阅读顺序：整体（P3–P7）→ 局部（P8–P20）→ 总结（P21）；每页右下角标注模块色。",
          "size": 9.5, "color": RGBColor(0x64, 0x74, 0x8B)}])

    # right-hand layered motif
    layers = [("接入", BLUE), ("指令", TEAL), ("存储", AMBER), ("网络", PURPLE)]
    by = 1.72
    for i, (name, col) in enumerate(layers):
        p = panel(s, 9.55, by, 2.6, 0.66, fill=NAVY2, line=col, lw=1.2, radius=0.12)
        ptext(p, [{"t": f"{i + 1:02d}  {name}层", "size": 10.0, "color": col, "bold": True,
                   "align": PP_ALIGN.CENTER}], anchor=MSO_ANCHOR.MIDDLE)
        if i < 3:
            arrow(s, 10.85, by + 0.68, 10.85, by + 0.95, color=RGBColor(0x47, 0x55, 0x69), w=1.2)
        by += 0.97
    txt(s, 9.55, by + 0.02, 2.6, 0.4,
        [{"t": "同一套指令模型", "size": 8.5, "color": RGBColor(0x94, 0xA3, 0xB8),
          "align": PP_ALIGN.CENTER}])
    notes(s, "封面。本 PPT 按代码实际调用链整理：先给整体架构与运行时序，再逐个子系统拆解，最后落到测试与验证边界。")
    return s


# ============================================================= S2 overview ===
def slide_overview():
    n = 2
    s = new_slide("整体 · OVERVIEW", "一页看懂：这个模组在解决什么问题，怎么跑起来",
                  "一句话：以「页」为单位的超大物品仓库——所有读写走指令，所有落盘走提交日志。",
                  BLUE, n, "先建立整体印象：三条主线分别对应线程模型、存储模型和联机模型，后面每一页都在展开其中一条。")

    # statement card
    p = panel(s, 0.6, 1.62, 8.28, 1.34, fill=BLUE_L, line=None, radius=0.06)
    txt(s, 0.82, 1.74, 7.9, 1.1,
        [{"t": "“把上万件物品装进一个仓库，同时保证崩溃、断线、重连之后一件不丢。”",
          "size": 14.5, "color": BLUE_D, "bold": True, "line": 1.25},
         {"t": "为此，模组把游戏里的物品操作抽象成 6 种指令，并把仓库拆成 1024 槽一页的页文件。",
          "size": 10.0, "color": BODY, "space_before": 5, "line": 1.2}])

    rows = [
        ("01", "一切物品操作都是「指令」", "UI、/li、拾取、补货、联机请求都只是把指令压入队列；"
                                           "真正的执行只发生在 Terraria 主线程，Item 永远不会被后台线程碰到。",
         TEAL, TEAL_L),
        ("02", "内存缓存摘要，数据落在页文件", "页可按需装载/卸载，摘要常驻；脏页在主线程生成字节快照，"
                                              "由「暂存目录 → 改名」的提交协议一次性发布，崩溃后自动重放。",
         AMBER, AMBER_L),
        ("03", "联机由服务端权威执行 + 幂等账本", "客户端只发请求；服务端执行并把结果写进事务账本，"
                                                  "同一 request-id 永远返回同一结果，交付靠「授予 + 回执」两步确认。",
         PURPLE, PURPLE_L),
    ]
    y = 3.16
    for num, title, desc, col, col_l in rows:
        badge = panel(s, 0.6, y + 0.06, 0.5, 0.5, fill=col_l, line=None, shape=MSO_SHAPE.OVAL)
        ptext(badge, [{"t": num, "size": 10.5, "color": col, "bold": True, "align": PP_ALIGN.CENTER}])
        txt(s, 1.28, y, 7.6, 0.42, [{"t": title, "size": 13.0, "color": INK, "bold": True}])
        txt(s, 1.28, y + 0.42, 7.6, 0.62, [{"t": desc, "size": 9.6, "color": MUT, "line": 1.18}])
        y += 1.16

    # route card
    p = panel(s, 9.12, 1.62, 3.61, 5.16, fill=CARD, line=LINE, radius=0.05)
    txt(s, 9.34, 1.8, 3.2, 0.35, [{"t": "阅读路线", "size": 11.5, "color": INK, "bold": True}])
    route = [
        ("P3–P4", "整体架构与运行时序", BLUE),
        ("P5–P7", "代码地图与关键参数", SLATE),
        ("P8–P11", "启动、每帧、指令流水线", TEAL),
        ("P12–P15", "存储模型与提交协议", AMBER),
        ("P16–P18", "单机交互与联机交付", PURPLE),
        ("P19–P21", "页修复、测试与总结", GREEN),
    ]
    yy = 2.3
    for pages, label, col in route:
        p2 = panel(s, 9.34, yy, 0.8, 0.3, fill=WHITE, line=col, lw=0.8, radius=0.4)
        ptext(p2, [{"t": pages, "size": 8.2, "color": col, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.02, 0.02, 0.01, 0.01))
        txt(s, 10.22, yy + 0.01, 2.3, 0.3, [{"t": label, "size": 9.2, "color": BODY}])
        yy += 0.53
    hline(s, 9.34, 5.55, 12.5, color=LINE)
    txt(s, 9.34, 5.7, 3.2, 0.9,
        [{"t": "配色约定", "size": 9.0, "color": INK, "bold": True, "space_after": 3},
         {"runs": [("接入", {"color": BLUE, "bold": True, "size": 8.6}),
                   (" / ", {"color": FAINT, "size": 8.6}),
                   ("指令", {"color": TEAL, "bold": True, "size": 8.6}),
                   (" / ", {"color": FAINT, "size": 8.6}),
                   ("存储", {"color": AMBER_T, "bold": True, "size": 8.6}),
                   (" / ", {"color": FAINT, "size": 8.6}),
                   ("网络", {"color": PURPLE, "bold": True, "size": 8.6})],
          "size": 8.6},
         {"runs": [("修复", {"color": RED, "bold": True, "size": 8.6}),
                   (" / ", {"color": FAINT, "size": 8.6}),
                   ("测试", {"color": GREEN, "bold": True, "size": 8.6})],
          "size": 8.6}])


# ========================================================== S3 architecture ===
def slide_arch():
    n = 3
    s = new_slide("整体 · ARCHITECTURE", "整体架构：六层各司其职，纵向调用、横向联机",
                  "从上到下是同一条调用链；网络层把同一套指令模型搬到服务端，形成「客户端请求 → 服务端权威执行」。",
                  BLUE, n, "记住这张分层图：后面每个局部都能对应到其中一层。接入层负责把游戏事件翻译成指令，指令层负责安全执行，存储层负责持久化。")

    bands = [
        ("运行时", SLATE, CARD2,
         ["Terraria.Main 主线程", "Mod / ModSystem / ModPlayer / GlobalItem", "On_ 钩子：存档 · 退出 · 删除 · 拾取"]),
        ("① 接入层", BLUE, BLUE_L,
         ["LargerInventory（Mod，网络包入口）", "InventorySystem（中枢）", "InventoryPlayer / LIPlayer",
          "InventoryGlobalItem", "InventoryDebugCommand"]),
        ("② 指令层", TEAL, TEAL_L,
         ["Command / CommandFactory", "CommandQueue（FIFO + 截止时间）", "CommandExecutor（主线程执行）", "Serializer（编解码）"]),
        ("③ 存储层", AMBER_T, AMBER_L,
         ["InventoryStore（门面）", "PageCache / PageDirectory / ItemPage", "StoreMeta / PageFileStore",
          "StoreRedoJournal（提交点）", "TypeIndex / PageLockTable"]),
        ("④ 网络层", PURPLE, PURPLE_L,
         ["NetworkSystem（协议 v5）", "TransactionLedger（幂等账本）", "ServerDelivery（交付计划）",
          "InventoryClientState（回执）"]),
    ]
    y = 1.62
    for name, col, light, chips in bands:
        p = panel(s, 0.6, y, 12.13, 0.72, fill=light, line=None, radius=0.06)
        txt(s, 0.76, y + 0.05, 2.0, 0.62,
            [{"t": name, "size": 10.5, "color": col, "bold": True}],
            anchor=MSO_ANCHOR.MIDDLE)
        auto_chips(s, 2.55, y + 0.17, 0.38, chips, col)
        y += 0.82

    # bottom explanation
    p = panel(s, 0.6, 5.83, 12.13, 0.5, fill=WHITE, line=LINE, radius=0.04)
    txt(s, 0.78, 5.86, 11.8, 0.44,
        [{"runs": [("纵向：", {"bold": True, "color": INK, "size": 9.3}),
                   ("接入层产生指令 → 指令层在主线程执行 → 存储层写页与日志；", {"color": BODY, "size": 9.3}),
                   ("横向：", {"bold": True, "color": PURPLE, "size": 9.3}),
                   ("网络层把同样的指令交给服务端执行，结果再交回客户端。", {"color": BODY, "size": 9.3})],
          "size": 9.3}], anchor=MSO_ANCHOR.MIDDLE)

    # repair + support
    p = panel(s, 0.6, 6.46, 5.95, 0.52, fill=RED_L, line=None, radius=0.05)
    txt(s, 0.78, 6.47, 5.7, 0.5,
        [{"runs": [("修复  PageRepairService  ", {"bold": True, "color": RED, "size": 9.0}),
                   ("复用执行锁 + 页锁，每帧只修一页", {"color": BODY, "size": 9.0})]}],
        anchor=MSO_ANCHOR.MIDDLE)
    p = panel(s, 6.78, 6.46, 5.95, 0.52, fill=CARD2, line=None, radius=0.05)
    txt(s, 6.96, 6.47, 5.7, 0.5,
        [{"runs": [("配套  ", {"bold": True, "color": SLATE, "size": 9.0}),
                   ("StoreIdentity · PlayerItemDelivery · Config · Log · Debug", {"color": BODY, "size": 9.0})]}],
        anchor=MSO_ANCHOR.MIDDLE)


# ============================================================== S4 runtime ===
def slide_runtime():
    n = 4
    s = new_slide("整体 · RUNTIME", "整体运行流程：一次进世界 → 每帧 → 一次存档",
                  "主线程不变式：Item、ItemIO、tModLoader 钩子只在主线程发生；后台线程（存档、socket）只碰字节。",
                  BLUE, n, "这是全局时序。注意每帧的 PostUpdateEverything 是几乎所有工作的唯一驱动点，存档钩子只负责把已经准备好的字节提交。")

    rows = [
        ("①", "加载", "Mod.Load / InventorySystem.OnModLoad", BLUE, BLUE_L,
         "注册单例与 4 个 On_ 钩子（存档、退出、删角色、删世界）；GlobalItem 注册拾取钩子；测试命令按服务器配置启用。"),
        ("②", "进世界", "PostWorldLoad / OnEnterWorld", TEAL, TEAL_L,
         "解析 .LI 目录、校验 identity、打开世界仓；单人再打开玩家仓，联机客户端改发 Hello 等待 Welcome。"),
        ("③", "每帧", "PostUpdateEverything", AMBER_T, AMBER_L,
         "各仓 Pump：重放 → 校准 → 修复 → 执行指令 → 脏页快照 → LRU；随后网络更新、主线程动作、待交付重试。"),
        ("④", "保存", "SavePlayer / SaveWorldData / 60s", PURPLE, PURPLE_L,
         "FlushForSave 拿执行锁，把页 + store.meta（+ 联机账本）作为一次目录改名提交。"),
        ("⑤", "退出 / 删除", "OnWorldUnload / Erase*", RED, RED_L,
         "先中断并排空指令（Put 原物归还）→ Flush → 关仓；确认存档与模组数据已删除后，把 .LI 送系统回收站。"),
    ]
    y = 1.62
    for num, name, path, col, light, desc in rows:
        p = panel(s, 0.6, y, 12.13, 0.92, fill=WHITE, line=LINE, radius=0.05)
        seg = panel(s, 0.6, y, 2.36, 0.92, fill=light, line=None, radius=0.05)
        txt(s, 0.78, y + 0.13, 0.4, 0.3, [{"t": num, "size": 11, "color": col, "bold": True}])
        txt(s, 1.18, y + 0.11, 1.68, 0.34, [{"t": name, "size": 11.5, "color": col, "bold": True}])
        txt(s, 0.78, y + 0.47, 2.05, 0.34, [{"t": path, "size": 7.2, "color": MUT, "font": MONO}])
        txt(s, 3.12, y + 0.12, 9.4, 0.72, [{"t": desc, "size": 9.5, "color": BODY, "line": 1.22}],
            anchor=MSO_ANCHOR.MIDDLE)
        if y < 5.7:
            arrow(s, 1.5, y + 0.94, 1.5, y + 1.02, color=FAINT, w=1.0)
        y += 1.04


# ============================================================= S5 code map1 ===
def slide_map1():
    n = 5
    s = new_slide("代码地图 · 一", "代码地图（一）：入口、配置与接入层",
                  "这些文件是模组与游戏之间的“翻译层”：把游戏事件变成指令，把指令结果交回游戏。",
                  SLATE, n, "文件很多，但只有接入层会直接接触游戏对象。看代码时先找 InventorySystem，它是所有回调的集合点。")

    cols = [
        ("入口与配置", BLUE, BLUE_L, [
            ("LargerInventory.cs", "Mod 主类：Load 记录日志；HandlePacket 转交 NetworkSystem。", True),
            ("LargerInventoryConfig.cs", "服务器权威开关：测试命令 / 自动拾取 / 自动补货，默认全关。", True),
            ("StoreConfig.cs", "存储运行参数：缓存 16 页、批 256、队列 4096、锁超时 5s。", True),
            ("Log.cs / Debug.cs", "统一 [LI] 日志；调试模式与内部异常上报。", True),
            ("StoreScope.cs", "仓库作用域：Player / World。", True),
        ]),
        ("接入：进程内", BLUE, BLUE_L, [
            ("InventorySystem.cs", "中枢 ModSystem：开/关仓、每帧泵、4 个存档删除钩子。", False),
            ("InventoryPlayer.cs", "ModPlayer：持久 UUID、回执、待交付；OnEnterWorld 初始化。", False),
            ("LIPlayer.cs", "兼容 0.1 旧存档：解码 _items，只迁移一次。", False),
            ("InventoryGlobalItem.cs", "自动拾取溢出 + 消耗后自动补货两个钩子。", False),
            ("PlayerItemDelivery.cs", "交付到背包 / 掉落 / 待交付重试（不丢物品兜底）。", False),
        ]),
        ("接入：身份与联机", BLUE, BLUE_L, [
            ("StoreIdentity.cs", ".LI 目录定位、li-identity 标记、改名跟随、回收站。", False),
            ("InventoryClientState.cs", "客户端回执持久化；校验并应用服务端交付计划。", False),
            ("InventoryDebugCommand.cs", "/li 测试命令：fill/take/q/slot/move/stats/repair/net*。", False),
            ("PageRequestRegistry.cs", "记录玩家访问过的页，供修复后通知。", False),
            ("调用顺序", "OnModLoad → PostWorldLoad → OnEnterWorld → PostUpdateEverything → Save", False),
        ]),
    ]

    x0 = 0.6
    cw = 3.94
    gap = 0.155
    for ci, (head, col, light, items) in enumerate(cols):
        x = x0 + ci * (cw + gap)
        hd = panel(s, x, 1.62, cw, 0.4, fill=col, line=None, radius=0.06)
        ptext(hd, [{"t": head, "size": 10.0, "color": WHITE, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.05, 0.05, 0.02, 0.02))
        y = 2.12
        rh = (6.42 - y) / len(items)
        for fname, desc, _ in items:
            p = panel(s, x, y, cw, rh - 0.08, fill=WHITE, line=LINE, radius=0.04)
            txt(s, x + 0.12, y + 0.05, cw - 0.24, 0.24,
                [{"t": fname, "size": 8.8, "color": col, "bold": True, "font": MONO}])
            txt(s, x + 0.12, y + 0.3, cw - 0.24, rh - 0.42,
                [{"t": desc, "size": 8.4, "color": MUT, "line": 1.15}])
            y += rh

    p = panel(s, 0.6, 6.52, 12.13, 0.46, fill=BLUE_L, line=None, radius=0.05)
    txt(s, 0.78, 6.53, 11.8, 0.44,
        [{"runs": [("共同约定：", {"bold": True, "color": BLUE_D, "size": 9.2}),
                   ("接入层不直接改仓库数据，只构造 Command 并 Store.Push；完成通知只走 callback，"
                    "线程与失败语义因此集中在一处。", {"color": BODY, "size": 9.2})]}],
        anchor=MSO_ANCHOR.MIDDLE)


# ============================================================= S6 code map2 ===
def slide_map2():
    n = 6
    s = new_slide("代码地图 · 二", "代码地图（二）：指令层与存储层",
                  "指令层负责“安全地改”，存储层负责“可靠地存”；两者通过页号与页锁衔接。",
                  SLATE, n, "存储层文件最多，但职责可以按内存侧和落盘侧分开看。Commit 相关只有 StoreRedoJournal 一个文件。")

    cols = [
        ("指令层（teal）", TEAL, [
            ("Command.cs", "指令模型：Kind / Access / Payload / 超时 / callback / 状态位。"),
            ("CommandQueue.cs", "FIFO + 最早截止时间；timeout=0 视为边界指令立即取。"),
            ("CommandExecutor.cs", "主线程执行器：校验、页锁、6 种执行、摘要同步、回调。"),
            ("CommandSerializer.cs", "指令 TagCompound 编解码（公开 API）。"),
            ("CommandResultSerializer.cs", "结果编解码；网络结果包也用它。"),
        ]),
        ("存储层 · 内存侧（amber）", AMBER_T, [
            ("InventoryStore.cs", "门面：Push/Pump/Flush、页与摘要、事务提交。"),
            ("PageCache.cs", "页装载、pin、LRU、脏页字节快照。"),
            ("PageDirectory.cs", "2048×1024 页目录；只分配不存在的页号。"),
            ("ItemPage.cs", "一页 1024 槽；可卸载，摘要常驻。"),
            ("PageSummary.cs", "占用位图、槽类型、类型统计、卸载物品。"),
            ("PageLockTable.cs", "页级独占锁；同一线程可重入。"),
            ("TypeIndex.cs", "全局类型聚合：类型 → 数量/收藏/页集合。"),
        ]),
        ("存储层 · 落盘侧（amber）", AMBER_T, [
            ("PageFileStore.cs", ".lipg 页文件：magic/版本/CRC/快速视图/物品 Tag；主备读取。"),
            ("StoreMeta.cs", "store.meta：页目录 + 每页摘要（v2，含 .bak）。"),
            ("StoreRedoJournal.cs", "提交点：暂存目录改名 → 发布 → 重放。"),
            ("RepairReport.cs", "修复统计结果对象。"),
            ("PageRepairService.cs", "页修复：每帧一页，归档后替换。"),
        ]),
    ]

    x0 = 0.6
    cw = 3.94
    gap = 0.155
    for ci, (head, col, items) in enumerate(cols):
        x = x0 + ci * (cw + gap)
        hd = panel(s, x, 1.62, cw, 0.4, fill=col, line=None, radius=0.06)
        ptext(hd, [{"t": head, "size": 10.0, "color": WHITE, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.05, 0.05, 0.02, 0.02))
        y = 2.12
        for fname, desc in items:
            p = panel(s, x, y, cw, 0.53, fill=WHITE, line=LINE, radius=0.04)
            txt(s, x + 0.12, y + 0.03, cw - 0.24, 0.22,
                [{"t": fname, "size": 8.6, "color": col, "bold": True, "font": MONO}])
            txt(s, x + 0.12, y + 0.25, cw - 0.24, 0.27,
                [{"t": desc, "size": 8.0, "color": MUT, "line": 1.08}])
            y += 0.6

    notes6 = [
        ("选页顺序", "强指定按槽位 → 弱指定查 TypeIndex → 索引关闭时扫描页摘要；弱指定可跳过忙页继续用其它页。"),
        (None, None),
        ("写入路径", "指令只改内存；落盘由主线程快照 → 提交日志 → 释放锁。读取先主后备，不覆盖不可替换的失败。"),
    ]
    for ci, (head, body) in enumerate(notes6):
        if head is None:
            continue
        x = x0 + ci * (cw + gap)
        p = panel(s, x, 5.24, cw, 1.18, fill=CARD, line=None, radius=0.04)
        txt(s, x + 0.14, 5.32, cw - 0.28, 0.26, [{"t": head, "size": 8.8, "color": AMBER_T, "bold": True}])
        txt(s, x + 0.14, 5.6, cw - 0.3, 0.76, [{"t": body, "size": 8.0, "color": MUT, "line": 1.14}])

    p = panel(s, 0.6, 6.5, 12.13, 0.5, fill=AMBER_L, line=None, radius=0.05)
    txt(s, 0.78, 6.51, 11.8, 0.48,
        [{"runs": [("衔接点：", {"bold": True, "color": AMBER_T, "size": 8.8}),
                   ("指令只改内存槽位并 MarkDirty；真正落盘只发生在 Flush / 事务提交。", {"color": BODY, "size": 8.8}),
                   ("　slotId = pageIndex << 10 | slot", {"color": AMBER_T, "bold": True, "size": 8.8,
                                                         "font": MONO})]}],
        anchor=MSO_ANCHOR.MIDDLE)


# ============================================================= S7 code map3 ===
def slide_map3():
    n = 7
    s = new_slide("代码地图 · 三", "代码地图（三）：网络、测试与关键参数",
                  "配套代码决定了“出问题能不能恢复、改动有没有被验证”；修复服务已在（二）列出。",
                  SLATE, n, "这一页把网络、测试和关键数字放在一起，方便后续对照。参数都来自 StoreConfig、NetworkSystem 与 PageDirectory 的常量。")

    # network
    x = 0.6
    p = panel(s, x, 1.62, 5.9, 0.4, fill=PURPLE, line=None, radius=0.06)
    ptext(p, [{"t": "网络层（protocol v5）", "size": 10.0, "color": WHITE, "bold": True,
               "align": PP_ALIGN.CENTER}], margins=(0.05, 0.05, 0.02, 0.02))
    net = [
        ("NetworkSystem.cs", "协议 v5：Hello/Welcome、请求/结果分帧、会话校验、事务执行。"),
        ("TransactionLedger.cs", "每仓持久账本：request-id → 指纹/结果，永不二次执行。"),
        ("ServerDelivery.cs", "服务端交付计划：grants(slot,before,after) + 余量掉落。"),
    ]
    y = 2.06
    for fname, desc in net:
        q = panel(s, x, y, 5.9, 0.8, fill=WHITE, line=LINE, radius=0.04)
        txt(s, x + 0.14, y + 0.07, 5.6, 0.24, [{"t": fname, "size": 9.0, "color": PURPLE, "bold": True, "font": MONO}])
        txt(s, x + 0.14, y + 0.32, 5.6, 0.44, [{"t": desc, "size": 8.4, "color": MUT, "line": 1.15}])
        y += 0.86

    # tests
    x2 = 6.83
    p = panel(s, x2, 1.62, 5.9, 0.4, fill=GREEN, line=None, radius=0.06)
    ptext(p, [{"t": "测试与旧实现（不参与模组编译）", "size": 10.0, "color": WHITE, "bold": True,
               "align": PP_ALIGN.CENTER}], margins=(0.05, 0.05, 0.02, 0.02))
    tests = [
        ("tests/.../Program.cs", "控制台测试入口：反射执行 24 项基础用例并汇总。"),
        ("StorageTests.cs", "7 项：提交重放、v1/v2 混合、归档、worker 提交、失败回滚。"),
        ("CommandTests.cs", "7 项：Put 全有或全无、忙页归还、重复提交、索引饱和恢复。"),
        ("NetworkTests.cs", "4 项：账本指纹冲突、旧条目只读、交付状态、回执鉴权。"),
        ("Backend/Legacy/*", "0.1 旧实现存档（6 个文件），仅作参考、不编译。"),
    ]
    y = 2.06
    for fname, desc in tests:
        q = panel(s, x2, y, 5.9, 0.76, fill=WHITE, line=LINE, radius=0.04)
        txt(s, x2 + 0.14, y + 0.06, 5.6, 0.22, [{"t": fname, "size": 8.8, "color": GREEN, "bold": True, "font": MONO}])
        txt(s, x2 + 0.14, y + 0.3, 5.6, 0.42, [{"t": desc, "size": 8.2, "color": MUT, "line": 1.13}])
        y += 0.84

    p = panel(s, 6.83, 6.2, 5.9, 0.68, fill=GREEN_L, line=None, radius=0.04)
    txt(s, 7.0, 6.22, 5.6, 0.64,
        [{"runs": [("规模：", {"bold": True, "color": GREEN, "size": 8.2}),
                   ("35 个模组源文件（存储 11 · 接入 9 · 入口配置 6 · 指令 5 · 网络 3 · 修复 1）",
                    {"color": BODY, "size": 8.2}),
                   ("　+ 测试 4 + 旧实现 6", {"color": MUT, "size": 8.2})]}],
        anchor=MSO_ANCHOR.MIDDLE)

    # constants
    p = panel(s, 0.6, 4.78, 5.9, 2.1, fill=CARD, line=LINE, radius=0.04)
    txt(s, 0.8, 4.86, 3.0, 0.26, [{"t": "关键参数速查", "size": 10.0, "color": INK, "bold": True}])
    consts = [
        ("1024", "每页槽位"), ("2048×1024", "页目录页数"),
        ("16 页", "内存页上限"), ("60s", "空闲淘汰"),
        ("256 × 16", "每批 × 每帧"), ("4096", "指令队列上限"),
        ("5000ms", "存档等锁"), ("24KB / 4MB", "分帧 / 结果上限"),
        ("16 / 60s", "在途请求 / 超时"), ("32768 / 32MB", "账本条数 / 容量"),
        ("2^21", "页号上限"), ("60s", "服务器定时存档"),
    ]
    for i, (val, label) in enumerate(consts):
        col = i % 2
        row = i // 2
        cx = 0.8 + col * 2.85
        cy = 5.2 + row * 0.27
        txt(s, cx, cy, 2.8, 0.26,
            [{"runs": [(val, {"size": 9.2, "color": AMBER_T, "bold": True, "font": MONO}),
                       ("　" + label, {"size": 7.8, "color": MUT})]}])


# ============================================================ S8 startup ====
def slide_startup():
    n = 8
    s = new_slide("局部 ① · 启动", "局部① 启动：模组加载 → 世界仓 → 角色仓",
                  "启动只做“解析目录 + 读纯数据”，耗时的页扫描与 Item 反序列化全部推迟到首帧。",
                  BLUE, n, "这一页对应 LargerInventory.cs、InventorySystem.cs、InventoryPlayer.cs、StoreIdentity.cs。注意构造 InventoryStore 时不会碰 ItemIO。")

    flow = [
        {"kicker": "tML", "title": "Mod.Load", "sub": "设置单例 / 日志", "fill": CARD2, "color": SLATE},
        {"kicker": "OnModLoad", "title": "挂 4 个钩子", "sub": "存档 · 退出 · 删档", "fill": BLUE_L, "color": BLUE_D},
        {"kicker": "PostWorldLoad", "title": "解析 .LI 目录", "sub": "校验 identity", "fill": BLUE_L, "color": BLUE_D},
        {"kicker": "new InventoryStore", "title": "恢复 + 读 meta", "sub": "不反序列化 Item", "fill": AMBER_L, "color": AMBER_T},
        {"kicker": "OnEnterWorld", "title": "角色仓 / Hello", "sub": "单人开仓 · 联机握手", "fill": PURPLE_L, "color": PURPLE},
    ]
    flow_row(s, 0.6, 1.62, 12.13, 0.92, flow, gap=0.28)

    cards = [
        ("世界仓目录解析", "StoreIdentity.OpenDirectory", BLUE, [
            "云存档 → SavePath/LargerInventory/Cloud/<scope>/<uuid>.LI",
            "本地 → 存档路径同名 .LI 目录",
            "li-identity 原子写入，校验归属；改名时跟随旧目录",
            "发现 reparse point / 他人 identity 直接拒绝",
        ]),
        ("InventoryStore 构造顺序", "构造期间只读纯数据", AMBER_T, [
            "① 建目录，创建页文件存储与提交日志",
            "② Recover()：先重放上次未完成的提交",
            "③ 读 transaction.ledger（缺失则为空）",
            "④ 读 store.meta（主坏 → .bak；都坏 → 标记重建）",
            "⑤ 建 PageCache / TypeIndex / Queue / Executor",
            "⑥ 标记待校准，等主线程 ReconcilePages",
        ]),
        ("角色与服务器绑定", "身份决定仓库归属", PURPLE, [
            "单人：OpenLocalPlayer → 打开玩家仓，按需迁移 0.1 旧数据",
            "联机客户端：SendHello(uuid)，每 2s 重发直到 Welcome",
            "服务器：Hello → TryBindServerPlayer 打开 世界仓/Players/<uuid>",
            "Welcome 携带世界 UUID 与 token，后续请求都带 token",
        ]),
    ]
    x = 0.6
    cw = 3.94
    gap = 0.155
    for i, (head, sub, col, items) in enumerate(cards):
        cx = x + i * (cw + gap)
        p = panel(s, cx, 2.72, cw, 2.72, fill=WHITE, line=LINE, radius=0.05)
        panel(s, cx, 2.72, cw, 0.08, fill=col, line=None, radius=0.0, shape=MSO_SHAPE.RECTANGLE)
        txt(s, cx + 0.16, 2.86, cw - 0.32, 0.32, [{"t": head, "size": 10.2, "color": INK, "bold": True}])
        txt(s, cx + 0.16, 3.16, cw - 0.32, 0.22, [{"t": sub, "size": 7.8, "color": col, "font": MONO}])
        paras = [{"t": "• " + it, "size": 8.2, "color": MUT, "line": 1.16, "space_after": 3} for it in items]
        txt(s, cx + 0.16, 3.44, cw - 0.32, 1.88, paras)

    p = panel(s, 0.6, 5.62, 12.13, 1.14, fill=CARD, line=LINE, radius=0.04)
    txt(s, 0.82, 5.7, 2.6, 0.28, [{"t": "为什么这样拆分", "size": 9.5, "color": INK, "bold": True}])
    txt(s, 0.82, 5.99, 5.6, 0.7,
        [{"t": "tModLoader 的异步加载线程不能调用 ItemIO。构造阶段只读 TagCompound 形式的元数据，"
               "页文件扫描与物品还原留到主线程首帧完成。",
          "size": 8.6, "color": MUT, "line": 1.2}])
    hline(s, 6.68, 5.76, 6.68, color=LINE)
    txt(s, 6.86, 5.7, 2.8, 0.28, [{"t": "失败时怎么办", "size": 9.5, "color": INK, "bold": True}])
    txt(s, 6.86, 5.99, 5.7, 0.7,
        [{"t": "打不开仓只记录错误并保持游戏可玩；页文件坏但 meta 有记录 → 该页标 LoadFailed，"
               "保留页号等待 /li repair，不阻塞启动。",
          "size": 8.6, "color": MUT, "line": 1.2}])


# ============================================================== S9 tick =====
def slide_tick():
    n = 9
    s = new_slide("局部 ② · 主循环", "局部② 每帧主循环：所有事情都在主线程发生",
                  "入口只有一个：InventorySystem.PostUpdateEverything。它决定了“什么时候允许改数据”。",
                  BLUE, n, "这是全项目最关键的函数。读代码时按 PostUpdateEverything → InventoryStore.Pump → CommandExecutor.Pump 的路径跟进去即可。")

    # left: main steps
    p = panel(s, 0.6, 1.62, 7.35, 5.24, fill=WHITE, line=LINE, radius=0.04)
    steps = [
        ("①", "退出检查", "_leavingSinglePlayer 时跳过全部工作", SLATE),
        ("②", "延迟关仓", "每 60s 重试关闭仍有脏数据的旧 Store", SLATE),
        ("③", "仓库泵", "对每个打开的 Store 调 Pump()（右栏展开）", BLUE),
        ("④", "网络更新", "服务端 ≤16 个入站动作/帧 + 分帧发送；客户端重发 Hello、请求 60s 超时", PURPLE),
        ("⑤", "主线程动作", "拾取/补货等 EnqueueMainThreadAction 的回调在此执行", TEAL),
        ("⑥", "交付重试", "单人每 60 tick 重试待交付物品（先背包，再掉落）", AMBER_T),
        ("⑦", "服务器定时存档", "每 60s 对全部 Store FlushForSave", RED),
    ]
    y = 1.78
    for num, name, desc, col in steps:
        b = panel(s, 0.78, y + 0.05, 0.34, 0.34, fill=WHITE, line=col, lw=0.9, shape=MSO_SHAPE.OVAL)
        ptext(b, [{"t": num, "size": 8.5, "color": col, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.01, 0.01, 0.01, 0.01))
        txt(s, 1.24, y, 6.4, 0.28, [{"t": name, "size": 10.3, "color": INK, "bold": True}])
        txt(s, 1.24, y + 0.28, 6.4, 0.36, [{"t": desc, "size": 8.4, "color": MUT, "line": 1.12}])
        y += 0.71

    # right: pump internals
    p = panel(s, 8.12, 1.62, 4.61, 5.24, fill=CARD, line=LINE, radius=0.04)
    txt(s, 8.32, 1.74, 4.2, 0.3, [{"t": "store.Pump() 内部", "size": 10.5, "color": BLUE_D, "bold": True}])
    txt(s, 8.32, 2.02, 4.2, 0.24, [{"t": "在调用线程（Terraria 主线程）执行", "size": 7.8, "color": MUT}])
    items = [
        ("1", "拿执行锁", "TryAcquireForSave(0)，非阻塞；被占用就跳过本帧"),
        ("2", "EnsureReplayed()", "存在 .commit-pending 就先重放"),
        ("3", "ReconcilePages()", "首帧用页文件校准 store.meta 摘要"),
        ("4", "PageRepairService.Pump()", "修复任务每次只处理 1 页"),
        ("5", "CommandExecutor.Pump()", "≤16 批，每批 ≤256 条指令"),
        ("6", "CaptureDirtyPageSnapshots()", "把脏页序列化成字节（ItemIO 只在主线程）"),
        ("7", "PageCache.TrimToLimit()", "LRU 卸载干净且未 pin 的页"),
    ]
    y = 2.3
    for num, name, desc in items:
        txt(s, 8.32, y, 0.26, 0.26, [{"t": num, "size": 9.5, "color": BLUE, "bold": True, "font": MONO}])
        txt(s, 8.6, y - 0.02, 3.95, 0.26, [{"t": name, "size": 8.7, "color": INK, "bold": True, "font": MONO}])
        txt(s, 8.6, y + 0.23, 3.95, 0.32, [{"t": desc, "size": 7.9, "color": MUT, "line": 1.08}])
        y += 0.56
    p = panel(s, 8.32, 6.3, 4.21, 0.46, fill=BLUE_L, line=None, radius=0.05)
    txt(s, 8.42, 6.31, 4.0, 0.44,
        [{"t": "非阻塞设计：宁可少跑一帧，也不让主线程等锁。", "size": 8.2, "color": BLUE_D,
          "bold": True, "line": 1.05}], anchor=MSO_ANCHOR.MIDDLE)


# ========================================================== S10 command =====
def slide_command():
    n = 10
    s = new_slide("局部 ③ · 指令", "局部③ 指令生命周期：从 Push 到回调",
                  "六个指令种类共用一条队列和一把执行锁；失败语义在入队、执行、回调三处统一。",
                  TEAL, n, "这页对应 Command.cs / CommandQueue.cs / CommandExecutor.cs。重点看 ExecuteBatch 的 6 步和失败位图。")

    flow = [
        {"title": "生产者", "sub": "UI · /li · 拾取\n补货 · 联机请求", "fill": CARD2, "color": SLATE},
        {"title": "Push 入队", "sub": "一次性标记\n暂停/销毁/满 → 立即失败", "fill": TEAL_L, "color": TEAL},
        {"title": "CommandQueue", "sub": "FIFO + 最早截止\n边界指令立即执行", "fill": TEAL_L, "color": TEAL},
        {"title": "主线程批执行", "sub": "≤256 条/批\n≤16 批/帧", "fill": TEAL_L, "color": TEAL},
        {"title": "callback", "sub": "同线程回调\nPut 带余量归还原物", "fill": TEAL_L, "color": TEAL},
    ]
    flow_row(s, 0.6, 1.66, 12.13, 1.0, flow, gap=0.26)

    # batch pipeline
    p = panel(s, 0.6, 2.94, 12.13, 0.4, fill=WHITE, line=LINE, radius=0.04)
    txt(s, 0.78, 2.96, 3.0, 0.36, [{"t": "ExecuteBatch 内部（每批固定 6 步）", "size": 9.6, "color": TEAL, "bold": True}],
        anchor=MSO_ANCHOR.MIDDLE)
    pipe = [
        ("1", "Validate", "形状与槽位合法性"),
        ("2", "页锁升序", "强指定必须全拿到"),
        ("3", "Execute", "6 种指令"),
        ("4", "SyncSummaries", "重算摘要 + 索引"),
        ("5", "释放页锁", "异常也保证释放"),
        ("6", "NotifyCallback", "批结束后统一回调"),
    ]
    xx = 0.78
    bw = 1.86
    for i, (num, name, desc) in enumerate(pipe):
        p = panel(s, xx, 3.44, bw, 0.86, fill=CARD, line=TEAL, lw=0.7, radius=0.08)
        txt(s, xx + 0.08, 3.49, bw - 0.16, 0.2, [{"t": f"{num}  {name}", "size": 8.3, "color": TEAL, "bold": True}])
        txt(s, xx + 0.08, 3.72, bw - 0.16, 0.5, [{"t": desc, "size": 7.6, "color": MUT, "line": 1.05}])
        if i < 5:
            arrow(s, xx + bw + 0.02, 3.87, xx + bw + 0.19, 3.87, color=FAINT)
        xx += bw + 0.22

    cards = [
        ("失败即归还（不静默丢物品）", TEAL, [
            "队列满 → Throttled；人工暂停 → ManualInterrupt",
            "页被修复占用 → PageBusy；页读不出 → PageLoadFailed",
            "内部异常 → InternalError；其余按位组合 Partial",
            "Put 一律把全部输入原样放回结果；Take 返回空列表",
        ]),
        ("调度与去重", BLUE, [
            "TryMarkSubmitted 保证一条指令只被接受一次",
            "timeout=0 的边界指令立即执行，用于“排空后再继续”",
            "非 0 按最早截止时间出队，过期即失败",
            "回调在批结束、页锁释放后执行，避免重入",
        ]),
    ]
    x = 0.6
    for i, (head, col, items) in enumerate(cards):
        cx = x + i * 6.14
        p = panel(s, cx, 4.54, 5.99, 1.86, fill=WHITE, line=LINE, radius=0.04)
        txt(s, cx + 0.16, 4.64, 5.6, 0.3, [{"t": head, "size": 9.8, "color": col, "bold": True}])
        paras = [{"t": "• " + it, "size": 8.4, "color": MUT, "line": 1.14, "space_after": 2} for it in items]
        txt(s, cx + 0.16, 4.96, 5.66, 1.36, paras)

    p = panel(s, 0.6, 6.56, 12.13, 0.34, fill=CARD2, line=None, radius=0.04)
    txt(s, 0.78, 6.57, 11.8, 0.32,
        [{"runs": [("并发边界：", {"bold": True, "color": INK, "size": 8.4}),
                   ("执行与存档共用 _executeGate —— 同一时刻要么在改页，要么在提交页，不会交错。",
                    {"color": BODY, "size": 8.4})]}], anchor=MSO_ANCHOR.MIDDLE)


# ========================================================== S11 command 6 ===
def slide_kinds():
    n = 11
    s = new_slide("局部 ④ · 六种指令", "局部④ 六种指令：语义与回滚边界",
                  "强指定 = 明确槽位；弱指定 = 给类型/数量由执行器贪心选槽。两者在页锁与失败处理上不同。",
                  TEAL, n, "这张表是全项目的行为契约。特别注意 Put 是全有或全无，Take 只负责取出、交付由调用方完成。")

    rows = [
        ("指令", "输入 / 访问集", "执行语义", "失败与回滚"),
        ("QueryType", "类型（弱指定）", "查 TypeIndex；索引关闭时扫描页摘要，返回数量/收藏/页列表", "不修改数据；计数饱和时置 Saturated 标志"),
        ("QuerySlot", "槽位数组（强指定）", "逐槽读出 type/stack/favorited；坏页的槽位返回 Air 占位", "部分失败返回 Partial + PageLoadFailed，不抛异常"),
        ("Take", "类型 + 数量（+ 目标堆叠快照）", "按页/槽顺序贪心取，整堆移动或拆分；跳过收藏与不兼容目标", "不足 → NotEnough；返回实际取出列表，调用方负责交付"),
        ("Put", "物品列表（可 spill 新页）", "先合并到同类未满槽，再找空槽；空间不足可申请新页", "全有或全无：任一物品放不下 → 回滚全部槽位并归还输入"),
        ("Move", "源槽 + 目标槽（强指定）", "目标空则搬移；同类则合并；异类/已满拒绝", "两槽都不改；返回 TargetOccupied / TypeMismatch"),
        ("Escape", "槽位或类型 + 回调", "收集槽位中的物品，交给第三方回调处理", "回调异常 → EscapeError | InternalError，数据不变"),
    ]
    end_y = simple_table(s, 0.6, 1.64, 12.13, [1.55, 3.0, 4.55, 3.03], rows,
                         row_h=0.56, header_fill=TEAL, body_size=8.4, header_size=9.2)

    p = panel(s, 0.6, end_y + 0.16, 5.99, 1.28, fill=TEAL_L, line=None, radius=0.04)
    txt(s, 0.78, end_y + 0.24, 5.6, 0.28, [{"t": "强指定 vs 弱指定", "size": 9.6, "color": TEAL, "bold": True}])
    txt(s, 0.78, end_y + 0.54, 5.66, 0.82,
        [{"t": "强指定：必须一次性拿到全部页锁，拿不到直接 PageBusy；只允许动列出的槽位。",
          "size": 8.4, "color": BODY, "line": 1.16, "space_after": 2},
         {"t": "弱指定：可跳过忙碌页继续用其它页，因此可能返回 Partial。",
          "size": 8.4, "color": BODY, "line": 1.16}])
    p = panel(s, 6.74, end_y + 0.16, 5.99, 1.28, fill=CARD, line=LINE, radius=0.04)
    txt(s, 6.92, end_y + 0.24, 5.6, 0.28,
        [{"t": "为什么 Put 必须全有或全无", "size": 9.6, "color": INK, "bold": True}])
    txt(s, 6.92, end_y + 0.54, 5.66, 0.82,
        [{"t": "调用方已经假定物品被收下并可能销毁了来源；若仓库只收一半，另一半必须原样退回，"
               "否则就是永久丢失。卷回是按原槽位引用恢复的。",
          "size": 8.4, "color": MUT, "line": 1.16}])


# ========================================================== S12 storage =====
def slide_storage():
    n = 12
    s = new_slide("局部 ⑤ · 存储布局", "局部⑤ 存储布局：一个目录就是一套仓库",
                  "页文件存物品，store.meta 存摘要，提交日志保证两者总是一起生效。",
                  AMBER_T, n, "这页看磁盘上的东西。.LI 目录可以整体复制/备份；.commit-pending 的存在意味着上次提交没发布完，下次启动会自动重放。")

    # tree
    p = panel(s, 0.6, 1.64, 6.02, 3.24, fill=RGBColor(0x0F, 0x17, 0x2A), line=None, radius=0.04)
    txt(s, 0.78, 1.72, 5.6, 0.26, [{"t": "<存档路径>.LI/", "size": 9.2, "color": RGBColor(0x93, 0xC5, 0xFD),
                                    "bold": True, "font": MONO}])
    tree = [
        "├─ li-identity              \"Player:<uuid>\" / \"World:<uuid>\"",
        "├─ store.meta              页目录 + 每页摘要（v2，含 .bak）",
        "├─ pages/",
        "│   ├─ 0000000.lipg        页文件（主）",
        "│   ├─ 0000000.lipg.bak    上一代备份",
        "│   └─ …",
        "├─ transaction.ledger      联机事务账本（可无）",
        "├─ .commit-pending/        存在 = 有待重放的提交",
        "├─ .commit-stage-*/        正在暂存（瞬时）",
        "└─ repair-archive/<时间>/  修复前的原始字节",
    ]
    paras = [{"t": t, "size": 7.9, "color": RGBColor(0xC7, 0xD2, 0xFE), "font": MONO,
              "line": 1.24} for t in tree]
    txt(s, 0.78, 2.02, 5.7, 2.2, paras)
    txt(s, 0.78, 4.02, 5.65, 0.74,
        [{"t": "备份 = 复制整个 .LI 目录；存档删除被游戏确认后，本目录被送往系统回收站。",
          "size": 7.8, "color": RGBColor(0x94, 0xA3, 0xB8), "line": 1.2},
         {"t": "同一目录内的 .LI 改名会跟随存档，但绝不会占用属于其它 UUID 的目录。",
          "size": 7.8, "color": RGBColor(0x94, 0xA3, 0xB8), "line": 1.2}])

    # page format
    p = panel(s, 6.9, 1.64, 5.83, 3.24, fill=WHITE, line=LINE, radius=0.04)
    txt(s, 7.1, 1.74, 5.4, 0.28, [{"t": "页文件 .lipg（v1 / v2 可按文件混合读取）", "size": 9.4, "color": AMBER_T, "bold": True}])
    segs = [
        ("MAGIC", "4B", "0x4750494C「LIPG」"),
        ("VER / FLAGS", "2+2B", "1 或 2；未知版本拒绝覆盖"),
        ("PAGE / CRC32", "4+4B", "页号校验；CRC 覆盖头部之后全部字节"),
        ("LEN / GUID", "4+4+16B", "quick 与 item 区长度 + 一致性 guid"),
        ("QUICK VIEW", "变长", "占用位图 16×u64、槽类型 1024×i32、类型统计"),
        ("ITEMS", "变长", "仅非空槽：slot + 长度 + ItemIO.Save 的 Tag"),
    ]
    yy = 2.06
    for name, size, desc in segs:
        txt(s, 7.1, yy, 1.55, 0.24, [{"t": name, "size": 7.8, "color": INK, "bold": True, "font": MONO}])
        txt(s, 8.62, yy, 0.86, 0.24, [{"t": size, "size": 7.8, "color": FAINT, "font": MONO}])
        txt(s, 9.5, yy, 3.1, 0.24, [{"t": desc, "size": 7.8, "color": MUT}])
        yy += 0.4
    txt(s, 7.1, 4.5, 5.4, 0.32,
        [{"t": "读取顺序：主文件 → 失败且可替换（Missing/Corrupt）→ 试 .bak；IoError、未知版本、模组错误都不覆盖。",
          "size": 7.6, "color": MUT, "line": 1.1}])

    # bottom three cards + summary
    cards = [
        ("分配器", "只分配目录中尚不存在的页号。磁盘恢复页与失败页永远保留原页号，不会被复用覆盖。", AMBER_T),
        ("主 / 备", "每次正常改写把旧主文件写为 .bak；主文件损坏时读 .bak，修复服务可把备份提升为主。", AMBER_T),
        ("提交日志", "一次提交可包含多页、store.meta 与账本；只要 marker 存在，重放就能把整批补齐。", AMBER_T),
    ]
    for i, (head, desc, col) in enumerate(cards):
        cx = 0.6 + i * 4.08
        p = panel(s, cx, 4.98, 3.94, 1.14, fill=CARD, line=LINE, radius=0.04)
        txt(s, cx + 0.16, 5.06, 3.6, 0.26, [{"t": head, "size": 9.4, "color": col, "bold": True}])
        txt(s, cx + 0.16, 5.34, 3.62, 0.72, [{"t": desc, "size": 8.2, "color": MUT, "line": 1.16}])

    p = panel(s, 0.6, 6.24, 12.13, 0.62, fill=AMBER_L, line=None, radius=0.04)
    txt(s, 0.8, 6.26, 11.75, 0.58,
        [{"runs": [("一句话：", {"bold": True, "color": AMBER_T, "size": 8.8}),
                   ("页文件是数据，store.meta 是目录，提交日志是把两者绑在一起的“原子开关”；"
                    "备份只是副本，恢复仍走同一条提交路径。", {"color": BODY, "size": 8.8})]}],
        anchor=MSO_ANCHOR.MIDDLE)


# ============================================================= S13 memory ====
def slide_memory():
    n = 13
    s = new_slide("局部 ⑥ · 内存模型", "局部⑥ 内存模型：摘要常驻，页按需装载",
                  "内存里始终只有“最近用过的少数页 + 全部页的摘要”，所以仓库大小不直接影响内存占用。",
                  AMBER_T, n, "这页对应 PageCache / PageDirectory / ItemPage / PageSummary / TypeIndex。关键区分：摘要（摘要统计）与页实体（Item 对象数组）。")

    # diagram
    p = panel(s, 0.6, 1.64, 6.7, 5.24, fill=WHITE, line=AMBER_T, lw=1.2, radius=0.04)
    txt(s, 0.8, 1.74, 4.0, 0.28, [{"t": "PageCache（每个 Store 一份）", "size": 10.0, "color": AMBER_T, "bold": True}])

    # directory
    p = panel(s, 0.82, 2.1, 3.0, 1.5, fill=AMBER_L, line=None, radius=0.05)
    txt(s, 0.94, 2.16, 2.8, 0.24, [{"t": "PageDirectory", "size": 8.8, "color": AMBER_T, "bold": True, "font": MONO}])
    txt(s, 0.94, 2.42, 2.8, 1.1,
        [{"t": "root[2048]", "size": 7.8, "color": BODY, "font": MONO, "line": 1.3},
         {"t": "  └ block[1024]", "size": 7.8, "color": BODY, "font": MONO, "line": 1.3},
         {"t": "      └ ItemPage", "size": 7.8, "color": BODY, "font": MONO, "line": 1.3},
         {"t": "只创建已存在的页；恢复页永不复用", "size": 7.2, "color": MUT, "line": 1.15}])

    p = panel(s, 3.98, 2.1, 3.1, 1.5, fill=TEAL_L, line=None, radius=0.05)
    txt(s, 4.1, 2.16, 2.9, 0.24, [{"t": "ItemPage", "size": 8.8, "color": TEAL, "bold": True, "font": MONO}])
    txt(s, 4.1, 2.42, 2.9, 1.1,
        [{"t": "Slots[1024] ← 可卸载", "size": 7.8, "color": BODY, "font": MONO, "line": 1.3},
         {"t": "Summary      ← 常驻", "size": 7.8, "color": BODY, "font": MONO, "line": 1.3},
         {"t": "Dirty / PinCount / LastUsed", "size": 7.8, "color": BODY, "font": MONO, "line": 1.3},
         {"t": "LoadFailed 页不参与查询", "size": 7.2, "color": MUT, "line": 1.15}])

    p = panel(s, 0.82, 3.76, 3.0, 1.5, fill=BLUE_L, line=None, radius=0.05)
    txt(s, 0.94, 3.82, 2.8, 0.24, [{"t": "PageSummary", "size": 8.8, "color": BLUE_D, "bold": True, "font": MONO}])
    txt(s, 0.94, 4.08, 2.8, 1.1,
        [{"t": "OccupiedBits[16] 位图", "size": 7.6, "color": BODY, "font": MONO, "line": 1.28},
         {"t": "SlotTypes[1024]", "size": 7.6, "color": BODY, "font": MONO, "line": 1.28},
         {"t": "Types: 类型 → 数量/收藏", "size": 7.6, "color": BODY, "font": MONO, "line": 1.28},
         {"t": "UnloadedTypes / Version", "size": 7.6, "color": BODY, "font": MONO, "line": 1.28}])

    p = panel(s, 3.98, 3.76, 3.1, 1.5, fill=PURPLE_L, line=None, radius=0.05)
    txt(s, 4.1, 3.82, 2.9, 0.24, [{"t": "TypeIndex（全局聚合）", "size": 8.8, "color": PURPLE, "bold": True, "font": MONO}])
    txt(s, 4.1, 4.08, 2.9, 1.1,
        [{"t": "type → 数量 / 收藏 / 页集合", "size": 7.6, "color": BODY, "font": MONO, "line": 1.28},
         {"t": "内部精确计数，输出时饱和", "size": 7.2, "color": MUT, "line": 1.15},
         {"t": "由 SyncSummaries 增量维护", "size": 7.2, "color": MUT, "line": 1.15},
         {"t": "关闭聚合时退化为扫描摘要", "size": 7.2, "color": MUT, "line": 1.15}])

    p = panel(s, 0.82, 5.4, 6.26, 0.54, fill=CARD2, line=None, radius=0.05)
    txt(s, 0.94, 5.42, 6.0, 0.5,
        [{"runs": [("快照：", {"bold": True, "color": INK, "size": 8.4}),
                   ("CaptureDirtySnapshots 把脏页编成 byte[]，存档线程只写这些字节，", {"color": BODY, "size": 8.4}),
                   ("ItemIO 绝不跨线程。", {"bold": True, "color": AMBER_T, "size": 8.4})]}],
        anchor=MSO_ANCHOR.MIDDLE)
    txt(s, 0.94, 6.1, 6.2, 0.6,
        [{"runs": [("一句话：", {"bold": True, "color": AMBER_T, "size": 8.4}),
                   ("摘要决定“去哪里找、还能放多少”，页实体只决定“现在能不能改”；"
                    "因此摘要必须常驻且可重算。", {"color": BODY, "size": 8.4})]}],
        anchor=MSO_ANCHOR.MIDDLE)

    rules = [
        ("装载", "EnsureLoaded：未加载 → 读页文件（主→.bak）→ 重建摘要 → 记录 backup 来源；坏页抛 PageLoadException 并标记 LoadFailed。", TEAL),
        ("修改", "指令改槽位 → MarkDirty；批次结束 SyncSummaries 重算该页摘要，并增量更新 TypeIndex。", AMBER_T),
        ("淘汰", "非脏 + 未 pin，且超过 16 页或空闲 60s → 释放 Slots；摘要保留，下次按需重载。", PURPLE),
        ("坏页", "LoadFailed 页从 TypeIndex 移除（不再算可用），但页号保留在 meta 与目录中，等修复。", RED),
        ("容量", "页目录 2048×1024 页，每页 1024 槽 ≈ 21 亿槽位；分配失败返回 NoSpace 而非崩溃。", SLATE),
    ]
    y = 1.64
    for head, desc, col in rules:
        p = panel(s, 7.54, y, 5.19, 0.92, fill=WHITE, line=LINE, radius=0.04)
        txt(s, 7.7, y + 0.08, 1.0, 0.26, [{"t": head, "size": 9.0, "color": col, "bold": True}])
        txt(s, 8.66, y + 0.08, 3.95, 0.78, [{"t": desc, "size": 8.2, "color": MUT, "line": 1.15}])
        y += 1.02


# ============================================================= S14 commit ====
def slide_commit():
    n = 14
    s = new_slide("局部 ⑦ · 提交协议", "局部⑦ 提交协议：一个目录改名就是提交点",
                  "StoreRedoJournal 把「写多个文件」变成「发布一个目录」：要么整批可见，要么下次重放。",
                  AMBER_T, n, "这是全项目最精巧的部分。请重点理解：rename 是唯一提交点；重放先校验后发布，所以任意时刻断电都不会写出半套数据。")

    steps = [
        ("1", "Stage 暂存", "在 .commit-stage-<guid>/ 写 manifest + 每个目标文件；逐文件 Flush(true)。", AMBER_T),
        ("2", "Commit 提交点", "重命名暂存目录即提交点，此后数据已提交。", RED),
        ("3", "Recover 重放", "先校验全部 payload（长度 + CRC），再逐个「写临时文件 + rename」发布；删除项直接删。", PURPLE),
        ("4", "Cleanup 清理", "marker 改名为 .commit-complete-*，再删除；绝不递归删除活动 marker。", SLATE),
        ("5", "Crash 恢复", "构造、EnsureReplayed、下次 Commit 前都会先 Recover；重放可重复执行，幂等。", GREEN),
    ]
    x = 0.6
    bw = 2.26
    gap = 0.21
    for i, (num, name, desc, col) in enumerate(steps):
        bx = x + i * (bw + gap)
        p = panel(s, bx, 1.66, bw, 0.92, fill=WHITE, line=col, lw=1.0, radius=0.06)
        b = panel(s, bx + 0.12, 1.76, 0.32, 0.32, fill=col, line=None, shape=MSO_SHAPE.OVAL)
        ptext(b, [{"t": num, "size": 9.0, "color": WHITE, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.01, 0.01, 0.01, 0.01))
        txt(s, bx + 0.5, 1.79, bw - 0.55, 0.3, [{"t": name, "size": 9.2, "color": INK, "bold": True}])
        txt(s, bx + 0.12, 2.1, bw - 0.24, 0.42, [{"t": desc, "size": 7.4, "color": MUT, "line": 1.1}])
        if i < 4:
            arrow(s, bx + bw + 0.02, 2.12, bx + bw + gap - 0.02, 2.12, color=FAINT)
        # detail under
        yy = 2.82

    details = [
        ("写入内容", "脏页快照\n旧主文件 → .bak\nstore.meta (+.bak)\ntransaction.ledger\nlegacy 导入标记"),
        ("原子性", "目标目录与暂存目录\n在同一分区；\nrename 不会跨卷。"),
        ("校验", "manifest 记录每个\n目标的长度与 CRC32；\n任一不符 → 拒绝发布。"),
        ("发布", "WriteNew + File.Move\n覆盖主文件；\n失败可重试。"),
        ("幂等", "重放只依赖 marker\n与 payload；\n中途崩溃可再次执行。"),
    ]
    for i, (head, body) in enumerate(details):
        bx = x + i * (bw + gap)
        p = panel(s, bx, 3.04, bw, 1.7, fill=CARD, line=LINE, radius=0.04)
        txt(s, bx + 0.14, 3.12, bw - 0.28, 0.26, [{"t": head, "size": 8.6, "color": INK, "bold": True}])
        lines = body.split("\n")
        paras = [{"t": ln, "size": 7.4, "color": MUT, "line": 1.2} for ln in lines]
        txt(s, bx + 0.14, 3.42, bw - 0.28, 1.24, paras)

    # bottom
    p = panel(s, 0.6, 4.94, 5.99, 1.84, fill=WHITE, line=LINE, radius=0.04)
    txt(s, 0.8, 5.04, 5.6, 0.28, [{"t": "一次提交包含什么", "size": 9.6, "color": AMBER_T, "bold": True}])
    txt(s, 0.8, 5.34, 5.0, 1.36,
        [{"t": "• 所有脏页的字节快照（同一代际）", "size": 8.4, "color": BODY, "line": 1.18, "space_after": 2},
         {"t": "• 每页主文件的新副本 + 上一代 .bak", "size": 8.4, "color": BODY, "line": 1.18, "space_after": 2},
         {"t": "• store.meta 与 store.meta.bak", "size": 8.4, "color": BODY, "line": 1.18, "space_after": 2},
         {"t": "• 可选的 transaction.ledger / 迁移标记", "size": 8.4, "color": BODY, "line": 1.18},
         {"t": "→ 一条提交记录最多约 2×页数 + 3 个目标。", "size": 8.0, "color": MUT, "line": 1.18}])

    p = panel(s, 6.74, 4.94, 5.99, 1.84, fill=RED_L, line=None, radius=0.04)
    txt(s, 6.94, 5.04, 5.6, 0.28, [{"t": "失败与崩溃语义", "size": 9.6, "color": RED, "bold": True}])
    txt(s, 6.94, 5.34, 5.6, 1.36,
        [{"t": "• marker 之前的任何失败：什么都不发布，内存回滚重试。", "size": 8.4, "color": BODY, "line": 1.18, "space_after": 2},
         {"t": "• marker 之后的失败：数据已提交，返回 false 并置 _needsReplay。", "size": 8.4, "color": BODY, "line": 1.18, "space_after": 2},
         {"t": "• 下次构造 / EnsureReplayed 先 Recover 再开放访问。", "size": 8.4, "color": BODY, "line": 1.18, "space_after": 2},
         {"t": "• 发布用同目录临时文件 + rename，避免半写文件。", "size": 8.4, "color": BODY, "line": 1.18}])


# ============================================================= S15 save =====
def slide_save():
    n = 15
    s = new_slide("局部 ⑧ · 保存与锁", "局部⑧ 保存时机与并发边界",
                  "保存只有一个入口 FlushForSave；它必须拿到执行锁，所以永远不会读到“执行到一半”的页。",
                  AMBER_T, n, "这页对应 InventoryStore.FlushForSave / CommitCore、InventorySystem 的钩子，以及 PageLockTable。理解三把锁即可理解全部并发。")

    rows = [
        ("触发点", "调用路径", "保存内容"),
        ("角色存档（单人）", "On_Player.SavePlayer → FlushStore(玩家仓)", "玩家仓全部脏页 + meta"),
        ("世界存档", "SaveWorldData → 所有非本地仓", "世界仓 + 服务端个人仓"),
        ("服务器定时", "PostUpdateEverything 每 60s", "全部打开的仓"),
        ("关闭 / 卸载", "TryDispose：先排空 / 中断指令，再 Flush", "尽量提交；失败保留脏页"),
        ("联机事务", "TryExecuteTransaction", "先提交旧脏页，再执行并连同账本提交"),
        ("修复", "CommitRepairPage", "单页 + meta，同一提交内发布"),
    ]
    y_end = simple_table(s, 0.6, 1.64, 7.28, [1.55, 3.25, 2.48], rows,
                         row_h=0.52, header_fill=AMBER_T, body_size=8.1, header_size=8.8)

    # lock diagram
    x = 8.08
    p = panel(s, x, 1.64, 4.65, 4.92, fill=WHITE, line=LINE, radius=0.04)
    txt(s, x + 0.2, 1.74, 4.2, 0.3, [{"t": "三把锁", "size": 10.2, "color": AMBER_T, "bold": True}])
    locks = [
        ("_executeGate", "执行 ↔ 存档互斥", "Pump TryEnter(0)\nFlush TryEnter(5000ms)", BLUE),
        ("PageLockTable", "页级独占（Monitor）", "指令整批持有到结束\n修复单页 TryEnter，拿不到就跳过", TEAL),
        ("_lifecycleGate", "Push ↔ Dispose 互斥", "销毁时不再接受新指令\n队列统一 ManualInterrupt", PURPLE),
    ]
    yy = 2.14
    for name, sub, desc, col in locks:
        p2 = panel(s, x + 0.2, yy, 4.25, 1.34, fill=CARD, line=LINE, radius=0.05)
        txt(s, x + 0.34, yy + 0.1, 3.9, 0.26, [{"t": name, "size": 9.2, "color": col, "bold": True, "font": MONO}])
        txt(s, x + 0.34, yy + 0.36, 3.9, 0.24, [{"t": sub, "size": 8.2, "color": INK, "bold": True}])
        txt(s, x + 0.34, yy + 0.62, 3.9, 0.66, [{"t": d, "size": 7.8, "color": MUT, "line": 1.14}
                                                 for d in desc.split("\n")])
        yy += 1.46

    p = panel(s, 0.6, y_end + 0.18, 7.28, 1.5, fill=CARD2, line=None, radius=0.04)
    txt(s, 0.8, y_end + 0.27, 6.9, 0.28, [{"t": "保存失败时", "size": 9.4, "color": INK, "bold": True}])
    txt(s, 0.8, y_end + 0.56, 6.95, 1.06,
        [{"t": "• 拿不到锁 / 提交异常 → 记录错误并返回 false；脏页保持 Dirty，下次继续尝试。",
          "size": 8.3, "color": MUT, "line": 1.14, "space_after": 2},
         {"t": "• 提交已过 marker → 数据其实已提交，只等重放；绝不覆盖回旧状态。",
          "size": 8.3, "color": MUT, "line": 1.14, "space_after": 2},
         {"t": "• 关闭世界时失败 → Store 留在 _closingStores，每 60s 重试。",
          "size": 8.3, "color": MUT, "line": 1.14}])


# ============================================================= S16 single ====
def slide_single():
    n = 16
    s = new_slide("局部 ⑨ · 单机交互", "局部⑨ 单机：拾取、补货、取出的闭环",
                  "三条链路都以「不丢物品」为底线：仓库放不下就掉落，掉不下就进待交付列表，随角色存档保存。",
                  PURPLE, n, "这页对应 InventoryGlobalItem.cs 与 PlayerItemDelivery.cs。所有钩子都只在单机且配置开启时生效。")

    lanes = [
        ("自动拾取", BLUE, [
            ("ItemSpace = true", "放行原版拾取，哪怕背包满"),
            ("PickupItem 余量", "钩子拿到原版没捡走的物品"),
            ("清空世界槽位", "防止重复拾取"),
            ("Put(spill=true)", "交给仓库，必要时开新页"),
            ("放不下 → 补偿", "掉落世界 / 进待交付"),
        ], "守卫：单人 + 自动拾取开 + 玩家存活 + 非受保护掉落 + 非暂停"),
        ("自动补货", TEAL, [
            ("OnConsumeItem", "消耗发生时触发"),
            ("快照 + 记录槽位", "记下原物品与目标槽"),
            ("主线程入队", "避免在钩子里直接改仓"),
            ("TakeCompatible", "按目标堆叠兼容取缺口量"),
            ("Refill + 回仓", "补进槽位；多余再 Put 回仓"),
        ], "守卫：单人 + 自动补货开 + maxStack>1 + 物品仍在背包 + 同一物品只排一次"),
        ("取出交付", AMBER_T, [
            ("Take 返回列表", "调用方负责交付"),
            ("Give 进背包", "先合并堆叠，再拆分成空槽"),
            ("多余掉落", "先确认有空的地面物品槽位"),
            ("仍失败 → Hold", "写入待交付列表"),
            ("每 60 tick 重试", "空间释放后自动补发"),
        ], "守卫：单机掉落；联机由服务器规划 grants 与掉落"),
    ]

    y = 1.62
    for name, col, steps, note in lanes:
        p = panel(s, 0.6, y, 1.38, 1.36, fill=col, line=None, radius=0.06)
        ptext(p, [{"t": name, "size": 10.5, "color": WHITE, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.05, 0.05, 0.02, 0.02))
        xx = 2.1
        bw = 1.97
        for i, (t1, t2) in enumerate(steps):
            q = panel(s, xx, y + 0.05, bw, 0.8, fill=WHITE, line=LINE, radius=0.06)
            txt(s, xx + 0.1, y + 0.1, bw - 0.2, 0.28, [{"t": t1, "size": 8.5, "color": col, "bold": True}])
            txt(s, xx + 0.1, y + 0.38, bw - 0.2, 0.44, [{"t": t2, "size": 7.4, "color": MUT, "line": 1.1}])
            if i < 4:
                arrow(s, xx + bw + 0.02, y + 0.45, xx + bw + 0.17, y + 0.45, color=FAINT, w=1.1)
            xx += bw + 0.19
        txt(s, 2.1, y + 0.9, 10.2, 0.3, [{"t": note, "size": 7.8, "color": MUT}])
        y += 1.72

    p = panel(s, 0.6, 6.8, 12.13, 0.3, fill=CARD2, line=None, radius=0.04)
    txt(s, 0.78, 6.81, 11.8, 0.28,
        [{"runs": [("兜底：", {"bold": True, "color": INK, "size": 8.2}),
                   ("待交付列表以 pendingDelivery 存进 ModPlayer，崩溃或退出后仍在；受保护掉落不会被自动拾取再次吞回。",
                    {"color": BODY, "size": 8.2})]}], anchor=MSO_ANCHOR.MIDDLE)


# ============================================================ S17 network ====
def slide_network():
    n = 17
    s = new_slide("局部 ⑩ · 联机请求", "局部⑩ 联机：协议 v5 的握手与请求",
                  "socket 线程只负责解码排队；世界、Item、事务一律回到主线程执行。",
                  PURPLE, n, "这页对应 NetworkSystem.cs 的前半部分。记住两条流水线：客户端请求/结果，以及服务端的“解码→排队→主线程执行→分帧回复”。")

    # actors
    p = panel(s, 0.8, 1.66, 3.1, 0.5, fill=BLUE_L, line=None, radius=0.08)
    ptext(p, [{"t": "客户端 MultiplayerClient", "size": 9.6, "color": BLUE_D, "bold": True, "align": PP_ALIGN.CENTER}])
    p = panel(s, 9.42, 1.66, 3.1, 0.5, fill=PURPLE_L, line=None, radius=0.08)
    ptext(p, [{"t": "服务端 Server（权威）", "size": 9.6, "color": PURPLE, "bold": True, "align": PP_ALIGN.CENTER}])
    vline(s, 2.35, 2.2, 5.42, color=RGBColor(0xBF, 0xDB, 0xFE), w=1.2, dash=DASH.DASH)
    vline(s, 10.97, 2.2, 5.42, color=RGBColor(0xDD, 0xD6, 0xFE), w=1.2, dash=DASH.DASH)

    msgs = [
        ("1", "→", "Hello(角色 UUID)　每 2s 重发，直到收到 Welcome", 2.55, BLUE),
        ("2", "←", "Welcome(玩家 UUID, 世界 UUID, token)", 3.12, PURPLE),
        ("3", "→", "Read / Mutate：scope + correlation + request + payload", 3.69, BLUE),
        ("4", "←", "Result：TagCompound，24KB 一帧分片传输", 4.26, PURPLE),
        ("5", "→", "Receipt：只对需要交付的结果回执", 4.83, BLUE),
    ]
    for num, d, label, y, col in msgs:
        x1, x2 = (2.4, 10.92) if d == "→" else (10.92, 2.4)
        arrow(s, x1, y, x2, y, color=col, w=1.5)
        b = panel(s, 1.9, y - 0.13, 0.3, 0.26, fill=WHITE, line=col, lw=0.8, shape=MSO_SHAPE.OVAL)
        ptext(b, [{"t": num, "size": 7.6, "color": col, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.01, 0.01, 0.01, 0.01))
        txt(s, 2.62, y - 0.31, 8.1, 0.27, [{"t": label, "size": 8.6, "color": BODY, "align": PP_ALIGN.CENTER}])

    p = panel(s, 0.8, 5.56, 11.72, 0.42, fill=CARD2, line=None, radius=0.05)
    txt(s, 0.96, 5.57, 11.4, 0.4,
        [{"runs": [("服务端内部：", {"bold": True, "color": PURPLE, "size": 8.4}),
                   ("HandlePacket 在 socket 线程解码 → Enqueue（epoch/revision/人数上限校验）→ 主线程 Update 逐条执行 → "
                    "会话校验后 Push 指令或走事务 → 结果分帧发送。", {"color": BODY, "size": 8.4})]}],
        anchor=MSO_ANCHOR.MIDDLE)

    notes_cards = [
        ("链路约束", PURPLE, ["协议版本 5，版本不符直接忽略", "结果 ≤4MB；每帧 24KB；≤8 个分片/帧",
                              "在途请求 ≤16；超时 60s → RequestTimeout", "服务端每帧最多处理 16 个入站动作"]),
        ("幂等键", BLUE, ["Key = (玩家 UUID, request-id)", "指纹 = SHA-256(操作类型 + 参数)",
                          "同 key 不同指纹 → InvalidArg", "同 key 同指纹 → 直接重放旧结果"]),
        ("Put 的特例", AMBER_T, ["物品不跨网络传输", "服务端 AuthorizePut 预授权来源（reserve/settle）",
                                "客户端只发 operation-id 引用", "测试用 netfill 走 DebugFill（需服务器配置）"]),
    ]
    for i, (head, col, items) in enumerate(notes_cards):
        cx = 0.8 + i * 3.93
        p = panel(s, cx, 6.1, 3.73, 0.98, fill=WHITE, line=LINE, radius=0.04)
        txt(s, cx + 0.12, 6.14, 3.5, 0.24, [{"t": head, "size": 8.6, "color": col, "bold": True}])
        txt(s, cx + 0.12, 6.38, 3.55, 0.66,
            [{"t": " · " + it, "size": 7.2, "color": MUT, "line": 1.1} for it in items])


# =========================================================== S18 delivery ====
def slide_delivery():
    n = 18
    s = new_slide("局部 ⑪ · 联机交付", "局部⑪ 联机取出：服务端规划、客户端回执",
                  "取出不是“把物品发过去”就完事：服务端先落账本，客户端验证槽位快照并保存回执，才算完成。",
                  PURPLE, n, "这页是整个联机流程的重点。10 步里 2、4、9 在服务端，6、7、8 在客户端；任何一步崩溃，重连后靠账本和回执继续。")

    top = [
        ("①", "客户端请求", "/li nettake → Take 请求\n带 request-id 与 SHA-256 指纹\n查询走 netq，不改数据", BLUE),
        ("②", "服务端执行", "TryExecuteTransaction\n先提交旧脏页 → 执行 Take\n页 + meta + 账本一次提交", PURPLE),
        ("③", "规划交付", "grants：slot / before / after\n用背包副本规划，先同步快照\n背包放不下转掉落计划", PURPLE),
        ("④", "计划落账", "先写 ledger 再发送\n未提交的计划对外不可见\n重试时直接重放旧结果", AMBER_T),
        ("⑤", "发送授予", "ServerGrant 结果分帧传输\n携带计划与状态\n客户端不回执就不算完成", PURPLE),
    ]
    bottom = [
        ("⑩", "断线 / 超时", "重连重新 Hello / Welcome\n同 request 永远返回同一结果\n余量可用 netcollect 再取", RED),
        ("⑨", "服务端确认", "校验 token 与 correlation\n清 grants / 标 Acknowledged\n提交账本后才释放所有权", PURPLE),
        ("⑧", "客户端回执", "Receipt：token + scope +\ncorrelation + request\n只发已完成的那一次", BLUE),
        ("⑦", "保存并校验", "Player.SavePlayer\n回读 .tplr 确认回执落盘\n失败则不发 Receipt", BLUE),
        ("⑥", "应用授予", "逐槽校验 before 指纹\n应用 after + SyncEquipment\n不匹配 → PageBusy", BLUE),
    ]
    bw = 2.26
    gap = 0.21
    for i, (num, name, desc, col) in enumerate(top):
        bx = 0.6 + i * (bw + gap)
        p = panel(s, bx, 1.66, bw, 1.5, fill=WHITE, line=col, lw=1.0, radius=0.06)
        txt(s, bx + 0.12, 1.74, 0.3, 0.26, [{"t": num, "size": 10, "color": col, "bold": True}])
        txt(s, bx + 0.44, 1.74, bw - 0.5, 0.26, [{"t": name, "size": 9.0, "color": INK, "bold": True}])
        txt(s, bx + 0.12, 2.06, bw - 0.24, 1.0, [{"t": desc, "size": 8.0, "color": MUT, "line": 1.16}])
        if i < 4:
            arrow(s, bx + bw + 0.02, 2.4, bx + bw + gap - 0.02, 2.4, color=FAINT)
    # snake connector: top-right ⑤ → bottom-right ⑥
    arrow(s, 11.61, 3.2, 11.61, 3.68, color=FAINT, w=1.3)

    for i, (num, name, desc, col) in enumerate(bottom):
        bx = 0.6 + (4 - i) * (bw + gap)
        p = panel(s, bx, 3.72, bw, 1.5, fill=WHITE, line=col, lw=1.0, radius=0.06)
        txt(s, bx + 0.12, 3.8, 0.3, 0.26, [{"t": num, "size": 10, "color": col, "bold": True}])
        txt(s, bx + 0.44, 3.8, bw - 0.5, 0.26, [{"t": name, "size": 9.0, "color": INK, "bold": True}])
        txt(s, bx + 0.12, 4.12, bw - 0.24, 1.0, [{"t": desc, "size": 8.0, "color": MUT, "line": 1.16}])
        if i < 4:
            arrow(s, bx - 0.02, 4.46, bx - gap + 0.02, 4.46, color=FAINT)

    p = panel(s, 0.6, 5.42, 5.99, 1.2, fill=PURPLE_L, line=None, radius=0.04)
    txt(s, 0.78, 5.5, 5.6, 0.28, [{"t": "为什么两步确认", "size": 9.4, "color": PURPLE, "bold": True}])
    txt(s, 0.78, 5.78, 5.66, 0.78,
        [{"t": "Terraria 把背包与模组数据分文件保存。客户端先落盘回执，服务端才敢清账；"
               "若中途崩溃，重连后不会重复发放，也不会把已发的物品再记一次。",
          "size": 8.2, "color": BODY, "line": 1.18}])
    p = panel(s, 6.74, 5.42, 5.99, 1.2, fill=CARD, line=LINE, radius=0.04)
    txt(s, 6.92, 5.5, 5.6, 0.28, [{"t": "三个容易忽略的细节", "size": 9.4, "color": INK, "bold": True}])
    txt(s, 6.92, 5.78, 5.7, 0.78,
        [{"t": "• 服务端对背包副本规划，先同步快照再改真背包", "size": 8.0, "color": MUT, "line": 1.14, "space_after": 1},
         {"t": "• 地面物品槽耗尽时不覆盖旧掉落，等空位重试", "size": 8.0, "color": MUT, "line": 1.14, "space_after": 1},
         {"t": "• 客户端余量可 /li netcollect 在空间足够时再取", "size": 8.0, "color": MUT, "line": 1.14}])


# ============================================================== S19 repair ===
def slide_repair():
    n = 19
    s = new_slide("局部 ⑫ · 页修复", "局部⑫ 修复：每次一页，先归档再替换",
                  "修复是破坏性操作，只允许单机执行；它与指令共用执行锁和页锁，所以不会改到正在使用中的页。",
                  RED, n, "这页对应 PageRepairService.cs。核心是“判定表”：主文件有效就跳过，主坏备好就提升备份，都坏才归档并写空白页。")

    steps = [
        ("触发", "/li repair [world]（单机 + 配置开启）", RED),
        ("开始", "拿执行锁；队列 = 磁盘页 ∪ 已分配页", RED),
        ("逐页", "每帧 1 页：TryEnter 页锁，失败重试 2 次后记 LockFailed", AMBER_T),
        ("跳过", "脏页 / 已加载且干净 → SkippedInUse，不碰内存数据", SLATE),
        ("判定", "读主文件与 .bak，按右表处理", PURPLE),
        ("完成", "回调 RepairReport；/li repair status / cancel", GREEN),
    ]
    y = 1.66
    for i, (name, desc, col) in enumerate(steps):
        p = panel(s, 0.6, y, 6.3, 0.62, fill=WHITE, line=LINE, radius=0.05)
        b = panel(s, 0.72, y + 0.14, 0.34, 0.34, fill=col, line=None, shape=MSO_SHAPE.OVAL)
        ptext(b, [{"t": str(i + 1), "size": 8.2, "color": WHITE, "bold": True, "align": PP_ALIGN.CENTER}],
              margins=(0.01, 0.01, 0.01, 0.01))
        txt(s, 1.18, y + 0.07, 1.15, 0.26, [{"t": name, "size": 9.2, "color": col, "bold": True}])
        txt(s, 2.3, y + 0.07, 4.45, 0.5, [{"t": desc, "size": 8.4, "color": MUT, "line": 1.1}],
            anchor=MSO_ANCHOR.MIDDLE)
        if i < len(steps) - 1:
            vline(s, 0.89, y + 0.63, y + 0.68, color=FAINT)
        y += 0.72

    rows = [
        ("读到的状态", "修复动作"),
        ("主文件有效", "Healthy：跳过，不写盘"),
        ("主文件坏 + .bak 有效", "Recoverable：.bak 升级为主（保留备份）"),
        ("两份都坏 / 缺失且页已知非空", "归档原始字节 → 提交同页号空白页"),
        ("IoError / 未知版本 / 模组物品失败", "保留待处理，不覆盖，计数上报"),
        ("页有脏数据 / 已加载干净页", "SkippedInUse：交给正常存档或跳过"),
    ]
    simple_table(s, 7.12, 1.66, 5.61, [2.5, 3.11], rows, row_h=0.58,
                 header_fill=RED, body_size=8.2, header_size=8.8)

    p = panel(s, 7.12, 5.28, 5.61, 1.5, fill=RED_L, line=None, radius=0.04)
    txt(s, 7.3, 5.36, 5.2, 0.28, [{"t": "安全边界", "size": 9.4, "color": RED, "bold": True}])
    txt(s, 7.3, 5.64, 5.25, 1.06,
        [{"t": "• 修复前把原始字节写入 repair-archive/<时间>-<guid>/", "size": 8.0, "color": BODY, "line": 1.14, "space_after": 2},
         {"t": "• 页号保留在 store.meta，不会被分配器复用", "size": 8.0, "color": BODY, "line": 1.14, "space_after": 2},
         {"t": "• 修复提交同样走提交日志，失败时恢复内存摘要", "size": 8.0, "color": BODY, "line": 1.14}])

    p = panel(s, 0.6, 6.12, 6.3, 0.68, fill=CARD, line=LINE, radius=0.04)
    txt(s, 0.78, 6.14, 5.95, 0.64,
        [{"runs": [("与指令互斥：", {"bold": True, "color": INK, "size": 8.4}),
                   ("修复运行期间，遇到被修复页的指令以 PageBusy 失败，不会阻塞主线程；"
                    "修复完成后再由服务器向请求过该页的玩家发通知。", {"color": BODY, "size": 8.4})]}],
        anchor=MSO_ANCHOR.MIDDLE)


# ============================================================== S20 tests ====
def slide_tests():
    n = 20
    s = new_slide("局部 ⑬ · 测试", "局部⑬ 回归测试：把“流程”变成可重复断言",
                  "42 项控制台用例覆盖存储、指令与账本；真实游戏行为与断电窗口仍需人工验证。",
                  GREEN, n, "这页对应 tests/LargerInventory.Tests。测试直接复用模组源码（InternalsVisibleTo），用隔离临时目录，不依赖游戏运行。")

    cards = [
        ("基础用例 · 24 项", BLUE, [
            "页目录：恢复页不复用、空洞可复用",
            "页存储：仅 .bak 的页可列出、可提升",
            "meta 保留失败页页号；坏页移出索引",
            "修复写空白页 / 页锁忙 / 暂停拒绝指令",
            "配置默认关闭；输入与结果类型分离",
            "序列化往返；交付进背包；Pump 线程；限流",
            "Dispose 排空、Put 被拒归还、非法槽位",
        ]),
        ("存储用例 · 7 项", AMBER_T, [
            "中断发布后重放：页与账本都恢复",
            "v1 旧页与新页混合读取，覆写时才升级",
            "双份损坏 → 归档两份原始字节 + 空白页",
            "未来版本页保持原样（不覆盖）",
            "worker 线程 Flush 提交全部脏页",
            "编码失败回滚页与账本；Dispose 失败保留脏页",
            "0.1 旧数据只导入一次（迁移标记）",
        ]),
    ]
    for i, (head, col, items) in enumerate(cards):
        cx = 0.6 + i * 4.14
        cw = 4.0 if i == 0 else 4.06
        p = panel(s, cx, 1.64, cw, 2.4, fill=WHITE, line=LINE, radius=0.04)
        panel(s, cx, 1.64, cw, 0.08, fill=col, line=None, radius=0.0, shape=MSO_SHAPE.RECTANGLE)
        txt(s, cx + 0.16, 1.78, cw - 0.32, 0.28, [{"t": head, "size": 9.8, "color": col, "bold": True}])
        paras = [{"t": "• " + it, "size": 7.9, "color": MUT, "line": 1.12, "space_after": 2} for it in items]
        txt(s, cx + 0.16, 2.12, cw - 0.32, 1.82, paras)

    # network / command cases, two columns
    p = panel(s, 0.6, 4.14, 8.2, 1.7, fill=WHITE, line=LINE, radius=0.04)
    panel(s, 0.6, 4.14, 8.2, 0.08, fill=PURPLE, line=None, radius=0.0, shape=MSO_SHAPE.RECTANGLE)
    txt(s, 0.76, 4.26, 4.0, 0.28, [{"t": "网络用例 · 4 项", "size": 9.4, "color": PURPLE, "bold": True}])
    txt(s, 4.96, 4.26, 3.7, 0.28, [{"t": "指令用例 · 7 项", "size": 9.4, "color": TEAL, "bold": True}])
    net_cases = [
        "账本指纹：改槽位/动作/数量/收藏即冲突",
        "旧账本条目只可查询、不可重试变更",
        "交付状态重载后保持（未完成/已完成）",
        "回执必须来自拥有者连接且对应已完成传输",
    ]
    cmd_cases = [
        "强 Put 不 spill；整批放不下则全部拒绝",
        "忙页/临时页 → 归还输入并释放临时页",
        "重复提交只结算一次；索引饱和可恢复",
    ]
    txt(s, 0.76, 4.58, 4.0, 1.18,
        [{"t": "• " + it, "size": 7.9, "color": MUT, "line": 1.12, "space_after": 2} for it in net_cases])
    txt(s, 4.96, 4.58, 3.7, 1.18,
        [{"t": "• " + it, "size": 7.9, "color": MUT, "line": 1.12, "space_after": 2} for it in cmd_cases])

    # right: how to run
    p = panel(s, 9.0, 1.64, 3.73, 4.2, fill=CARD, line=LINE, radius=0.04)
    txt(s, 9.18, 1.76, 3.45, 0.28, [{"t": "怎么跑", "size": 9.8, "color": GREEN, "bold": True}])
    txt(s, 9.18, 2.06, 3.4, 0.9,
        [{"t": "dotnet build LargerInventory.csproj \\", "size": 7.3, "color": INK, "font": MONO, "line": 1.25},
         {"t": "  -p:BuildMod=false -p:TargetFramework=net8.0", "size": 7.3, "color": INK, "font": MONO, "line": 1.25},
         {"t": "dotnet run --project tests/LargerInventory.Tests", "size": 7.3, "color": INK, "font": MONO, "line": 1.25}])
    hline(s, 9.18, 3.06, 12.55, color=LINE)
    txt(s, 9.18, 3.16, 3.45, 0.28, [{"t": "测试替身", "size": 9.0, "color": INK, "bold": True}])
    txt(s, 9.18, 3.44, 3.4, 1.0,
        [{"t": "隔离临时目录 + 真实 Store；网络层直接调用 CreateRequestFingerprint / "
               "TransactionLedger / CanAcknowledge，不依赖多人进程。", "size": 7.8, "color": MUT, "line": 1.16}])
    hline(s, 9.18, 4.5, 12.55, color=LINE)
    txt(s, 9.18, 4.6, 3.45, 0.28, [{"t": "仍需人工验证", "size": 9.0, "color": RED, "bold": True}])
    txt(s, 9.18, 4.88, 3.4, 0.8,
        [{"t": "真实 Item 序列化、拾取/补货钩子、联机交付、系统回收、物理断电。",
          "size": 7.8, "color": MUT, "line": 1.16}])

    p = panel(s, 0.6, 5.98, 12.13, 0.9, fill=GREEN_L, line=None, radius=0.04)
    txt(s, 0.8, 6.06, 2.8, 0.28, [{"t": "验证边界", "size": 9.6, "color": GREEN, "bold": True}])
    txt(s, 0.8, 6.34, 11.7, 0.5,
        [{"t": "控制台回归能证明“提交重放、页恢复、指令回滚、账本幂等”这些确定性逻辑；"
               "但它不驱动 Terraria，因此游戏内流程按 MANUAL_TEST_PLAN 的 13 节逐项验收。",
          "size": 8.6, "color": BODY, "line": 1.18}])


# ============================================================ S21 summary ====
def slide_summary():
    n = 21
    s = new_slide("总结 · SUMMARY", "总结：一条主线，三个不变量",
                  "如果只记住一张图，就是这条从指令到提交的主线；如果只记住三条规则，就是下面这三个不变量。",
                  INK, n, "收尾。主线和三个不变量是理解整个项目的钥匙；入口速查给出继续阅读源码的路径。")

    # main line
    p = panel(s, 0.6, 1.64, 12.13, 0.4, fill=INK, line=None, radius=0.04)
    txt(s, 0.8, 1.66, 4.0, 0.36, [{"t": "一条主线", "size": 9.8, "color": WHITE, "bold": True}],
        anchor=MSO_ANCHOR.MIDDLE)
    chain = [
        ("指令", "Command + Push", TEAL_L, TEAL),
        ("主线程执行", "CommandExecutor", TEAL_L, TEAL),
        ("脏页快照", "PageCache", AMBER_L, AMBER_T),
        ("原子提交", "StoreRedoJournal", AMBER_L, AMBER_T),
        ("交付 / 回执", "Delivery + Ledger", PURPLE_L, PURPLE),
    ]
    xx = 0.8
    bw = 2.26
    for i, (t1, t2, fillc, colc) in enumerate(chain):
        p = panel(s, xx, 2.18, bw, 0.82, fill=fillc, line=None, radius=0.08)
        txt(s, xx + 0.12, 2.25, bw - 0.24, 0.3, [{"t": t1, "size": 10.0, "color": colc, "bold": True,
                                                   "align": PP_ALIGN.CENTER}])
        txt(s, xx + 0.12, 2.55, bw - 0.24, 0.3, [{"t": t2, "size": 7.4, "color": MUT, "font": MONO,
                                                   "align": PP_ALIGN.CENTER}])
        if i < 4:
            arrow(s, xx + bw + 0.03, 2.59, xx + bw + 0.18, 2.59, color=FAINT, w=1.3)
        xx += bw + 0.21

    invs = [
        ("不变量 ①", "主线程独占 Item", "所有 ItemIO、ItemLoader、Mod Hook 只在主线程；存档与 socket 线程只处理字节。违背它就会出现随机崩溃或物品错乱。", BLUE),
        ("不变量 ②", "唯一提交点", "只有 .commit-pending 目录改名算提交；之前失败 = 未发生，之后失败 = 已提交待重放。没有第三种状态。", AMBER_T),
        ("不变量 ③", "失败必归还", "Put 全有或全无并原物归还；Take 余量与掉落失败进待交付；联机靠账本 + 回执避免重复发放。", PURPLE),
    ]
    x = 0.6
    cw = 3.94
    gap = 0.155
    for i, (tag, head, desc, col) in enumerate(invs):
        cx = x + i * (cw + gap)
        p = panel(s, cx, 3.22, cw, 1.86, fill=WHITE, line=LINE, radius=0.05)
        panel(s, cx, 3.22, 0.1, 1.86, fill=col, line=None, radius=0.0, shape=MSO_SHAPE.RECTANGLE)
        txt(s, cx + 0.26, 3.32, cw - 0.4, 0.24, [{"t": tag, "size": 8.2, "color": col, "bold": True}])
        txt(s, cx + 0.26, 3.58, cw - 0.4, 0.34, [{"t": head, "size": 12.0, "color": INK, "bold": True}])
        txt(s, cx + 0.26, 3.98, cw - 0.42, 1.0, [{"t": desc, "size": 8.3, "color": MUT, "line": 1.18}])

    p = panel(s, 0.6, 5.26, 5.99, 1.74, fill=CARD, line=LINE, radius=0.04)
    txt(s, 0.8, 5.36, 5.6, 0.28, [{"t": "入口速查（按调用顺序）", "size": 9.4, "color": INK, "bold": True}])
    entries = [
        "LargerInventory.cs　Mod.Load / HandlePacket",
        "InventorySystem.cs　OnModLoad / PostWorldLoad",
        "InventorySystem.cs　PostUpdateEverything（每帧）",
        "InventoryStore.cs　Pump / FlushForSave",
        "CommandExecutor.cs　ExecuteBatch",
        "NetworkSystem.cs　HandlePacket / Update",
    ]
    txt(s, 0.8, 5.66, 5.6, 1.24,
        [{"t": "→ " + e, "size": 7.9, "color": BODY, "font": MONO, "line": 1.24} for e in entries])
    p = panel(s, 6.74, 5.26, 5.99, 1.74, fill=CARD, line=LINE, radius=0.04)
    txt(s, 6.94, 5.36, 5.6, 0.28, [{"t": "延伸阅读", "size": 9.4, "color": INK, "bold": True}])
    txt(s, 6.94, 5.66, 5.6, 1.24,
        [{"t": "• README.md — 功能、配置、构建方式", "size": 8.2, "color": BODY, "line": 1.24},
         {"t": "• Backend/IMPLEMENTATION.md — 后端实现约定", "size": 8.2, "color": BODY, "line": 1.24},
         {"t": "• PROJECT_AUDIT_AND_REMEDIATION.md — 修订史与已知风险", "size": 8.2, "color": BODY, "line": 1.24},
         {"t": "• MANUAL_TEST_PLAN.md — 游戏内 13 节验收流程", "size": 8.2, "color": BODY, "line": 1.24}])


# ------------------------------------------------------------------ build ----
def build():
    slide_cover()
    slide_overview()
    slide_arch()
    slide_runtime()
    slide_map1()
    slide_map2()
    slide_map3()
    slide_startup()
    slide_tick()
    slide_command()
    slide_kinds()
    slide_storage()
    slide_memory()
    slide_commit()
    slide_save()
    slide_single()
    slide_network()
    slide_delivery()
    slide_repair()
    slide_tests()
    slide_summary()

    prs.core_properties.title = "LargerInventory 代码运行流程全景"
    prs.core_properties.subject = "tModLoader 模组源码导读：先整体后局部"
    prs.core_properties.author = "LargerInventory 源码导读"
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "LargerInventory-代码运行流程.pptx")
    prs.save(out)
    print("saved:", out, "slides:", len(prs.slides))


if __name__ == "__main__":
    build()
