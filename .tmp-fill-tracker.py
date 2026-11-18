# -*- coding: utf-8 -*-
"""Fill the WorkTiles tracker. 人工测试过 stays blank for a person to check."""
import openpyxl
from openpyxl.comments import Comment
from openpyxl.styles import Alignment, Font
from openpyxl.utils import get_column_letter

path = r"C:\Users\SYSTE\Documents\WorkTiles改进.xlsx"

bugs = [
    ("退出编辑模式进入预览模式会导致有一个磁贴的标题是输入框状态，无法关闭", "退出编辑时收起标题输入框，不再留在输入状态"),
    ("缩放空间有个左右滑动的条，且缩放滑动条与字不在同一高度（没对齐）", "去掉缩放岛多余的横向滚动条，滑条和百分比对齐"),
    ("字和序号没对齐", "序号与正文第一行共用字号，并按上沿对齐"),
    ("这个控件也有左右滑动的条", "格式工具栏去掉多余的横向滚动条"),
    ("外观、组件设置里的预览在调节透明度和毛玻璃效果时预览很卡且一直闪黑", "拖动透明度和毛玻璃时复用原来的画笔，预览不再闪黑"),
    ("菜单栏避让上方应填充和菜单栏背景一样的颜色", "避让区域改用和菜单栏一样的背景色"),
    ("菜单栏的预览下显示关闭时，菜单栏这个部分也不应该显示", "预览里关闭菜单栏时，整条菜单栏一起收起"),
    ("软件各处包括图标和一些界面都使用紫色，应改为从 Windows 主题色提取", "品牌色和图标改读 Windows 主题色，不再写死紫色"),
    ("控制窗中所有滑块调节预览和真实控制窗都巨卡，滑块也巨卡", "滑块只刷新当前这块窗口和预览，不再整窗重画"),
    ("视频背景开启缩放后，预览和真实显示都带黑边，而且预览位置和真实位置有偏差", "视频缩放按画面裁切铺满，预览和实际使用同一套位置"),
    ("视频背景开启适应后，空出来的边不会像图片那样填充设置的背景颜色", "适应模式的留边显示所设置的背景色"),
    ("不要再为磁贴外观提供从 Wallpaper Engine 导入或者选择视频的能力", "磁贴外观只保留图片，去掉视频和 Wallpaper Engine"),
    ("磁贴预览有概率不是左右分屏，且选择图片有概率预览完全不变", "宽窗口里预览固定在右侧；换图后预览会更新"),
    ("磁贴使用图片背景时，仅在开启毛玻璃时颜色透明度滑块可用，并且每个滑块都很卡", "图片背景不开启毛玻璃也能调颜色和透明度，滑块不再整窗重刷"),
    ("缩放后自动排序仍按缩放前的位置排，缩小后有些地方有隐形墙，磁贴放不过去", "缩小后画板跟着可见范围变大，自动排列按缩放后的位置排"),
    ("时间组件在定宽时左边留空，导致中轴线不对", "时钟按可见内容居中，不再在时间左侧留空列"),
    ("天气和噪音内容不居中", "天气和噪音在组件栏里居中"),
    ("图片控件的裁切应与 Word 等正常软件一样", "裁切只移动取景框，不再把画面一起缩放"),
    ("图片控件旋转会导致图片不正常显示，且尺寸大幅异常变化", "旋转只交换宽高，不再把图片拉变形"),
    ("控制窗圆角效果和控制窗大小有关，最大圆角应为 20 并呈胶囊，且任何大小效果不变", "圆角按短边比例换算，20 档在任何尺寸都是胶囊"),
    ("第三方输入法（如微信输入法）无法显示输入框", ""),
    ("使用网页壁纸会导致毛玻璃效果直接变黑", "网页壁纸开毛玻璃时改为模糊预览图，不再采到黑底"),
    ("从 Wallpaper Engine 导入的壁纸应该使用壁纸名而非文件名做浏览", "导入后按 Wallpaper Engine 的壁纸名浏览"),
    ("动态壁纸也应截一帧缩略图做预览", "视频和网页壁纸用预览图做缩略图"),
    ("默认字号设为 20，但第一行输入却是 15", "没有单独保存过格式的正文连同行末段落标记一起套用默认字号，第一行不再落回 15"),
    ("行末还可以再往后选到一格空位，且这个空位的字号是 15", "行末那一格改用前一个字的字号，不再停在 15"),
]

improvements = [
    ("关于页里两个仓库应横向并排，Gitee 仍使用 EdgeHH/Pancake 作为仓库显示和更新源", "两个仓库横向并排，Gitee 的显示和更新源都用 EdgeHH/Pancake"),
    ("关于页加上实验性功能，可开启显示集控和作业填充（默认隐藏）", "集控和作业填充放进实验性功能，默认不显示"),
    ("考试页加一行：更进阶的考试功能请使用 ExamAware2", "考试页加上 ExamAware2 的说明"),
    ("设置页把外观里的时钟页移到组件里，把外观里的组件移到组件下新建的外观页", "时钟页挪到组件，组件外观单独一页"),
    ("设置页中菜单栏的图标可以和窗口设置的图标一致", "菜单栏设置图标与窗口设置一致"),
    ("控制窗位置选择和预览改窄一点，然后在同一排展示", "位置选择和预览改窄后排在同一行"),
    ("在布局页上方加“常规”页，进入设置时默认显示。含开机自启；开启后静默启动可用且默认开（开机不显示主界面，只留托盘；若桌面磁贴开启则显示磁贴）。桌面磁贴移到这里", ""),
    ("时钟提供“显示秒”开关，并支持右上角小数字或大数字完整显示（xx:xx:xx）", "秒数可选关闭、右上角小数字，或并入大数字"),
    ("托盘菜单参照给定界面：保留重启、退出、应用设置；显示桌面磁贴与隐藏桌面磁贴互斥；打开主界面。静默启动定义同上", ""),
    ("磁贴外观页加入磁贴圆角设置", "磁贴外观增加圆角"),
    ("布局页里的预览在切换网格间隔和排列间隔时卡", "网格和排列间隔的拖动合并到下一帧，预览不再堵住滑块"),
    ("自动排列间隔在开启吸附网格时，调整单位改为 0.5 格，显示也按 0.5 格而不是像素", "吸附网格时排列间隔按 0.5 格显示和调节"),
    ("主题色系：选择框与文字同行，下一行色卡；新建自定义色系等于克隆当前色系，点色卡可改该色", ""),
    ("窗口名称只显示 WorkTiles，不再显示班级作业板", "窗口标题只保留 WorkTiles"),
    ("磁贴外观页提供“右下角显示学科字样”开关", "磁贴外观增加右下角学科字样开关"),
    ("最小化时停止检测下面加一个“窗口失焦时停止监测”", "增加窗口失焦时停止监测"),
    ("删掉允许多实例。再次启动时弹出“检测到正在运行的 WorkTiles 实例”，选项为“退出正在运行的实例并启动”和“返回”", "同一目录只开一个窗口，再次启动先询问是否退出正在运行的实例"),
    ("致命错误不要直接未响应退出。显示错误窗口，提供重启、退出、忽略和复制错误信息", ""),
    ("网格间距最小值改大；若因缩放一次要渲染太多网格就不显示", "网格间距有下限，过密时不绘制"),
    ("不开启无限作业板时只支持 80%–140% 缩放，平移上下左右不超过半个屏幕", "固定作业板缩放限制在 80%–140%，平移不超过半屏"),
    ("目录改为根目录下 WorkTiles.exe、data、app-版本", ""),
    ("保存应在导入和导出上面，导入和导出相邻", "文件菜单里保存放在导入和导出上面"),
    ("科目补全改为自动开启的内容", "科目补全改为默认开启"),
]

font = "等线"
body = Font(name=font, size=11)
title_font = Font(name=font, size=14, bold=True)
header_font = Font(name=font, size=11, bold=True)
wrap = Alignment(wrap_text=True, vertical="center")
center = Alignment(horizontal="center", vertical="center", wrap_text=True)

wb = openpyxl.load_workbook(path)

def fill(ws, title, rows):
    if "A1:D1" not in [str(r) for r in ws.merged_cells.ranges]:
        ws.merge_cells("A1:D1")
    ws["A1"] = title
    ws["A1"].font = title_font
    ws["A1"].alignment = Alignment(horizontal="center", vertical="center")
    headers = ["序号", "问题内容", "完成改动", "人工测试过"]
    for col, text in enumerate(headers, 1):
        cell = ws.cell(2, col, text)
        cell.font = header_font
        cell.alignment = center
    # Drop leftover rows from the 16-row template when the list is longer, and clear extras if shorter.
    last = 2 + len(rows)
    if ws.max_row > last:
        ws.delete_rows(last + 1, ws.max_row - last)
    for i, (problem, change) in enumerate(rows, 1):
        row = 2 + i
        number = ws.cell(row, 1, f"#{i}")
        number.font = body
        number.alignment = center
        problem_cell = ws.cell(row, 2, problem)
        problem_cell.font = body
        problem_cell.alignment = wrap
        change_cell = ws.cell(row, 3, change or None)
        change_cell.font = body
        change_cell.alignment = wrap
        tested = ws.cell(row, 4, None)
        tested.font = body
        tested.alignment = center
        ws.row_dimensions[row].height = 36 if len(problem) < 40 else 48
    ws.column_dimensions["A"].width = 8
    ws.column_dimensions["B"].width = 62
    ws.column_dimensions["C"].width = 46
    ws.column_dimensions["D"].width = 14
    ws.row_dimensions[1].height = 24
    ws.row_dimensions[2].height = 22
    ws.auto_filter.ref = f"A2:D{2 + len(rows)}"
    ws.auto_filter.ref = None  # header row is enough; avoid a filter the user did not ask for
    note = Comment("这一列留给人工勾选。自动界面验证通过不会写在这里。", "WorkTiles")
    note.width = 220
    note.height = 50
    ws["D2"].comment = note

fill(wb["Sheet1"], "WorkTiles BUG", bugs)
fill(wb["Sheet2"], "WorkTiles 改进", improvements)
wb.save(path)
print("rows", len(bugs), len(improvements))
