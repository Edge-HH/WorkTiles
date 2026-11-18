# -*- coding: utf-8 -*-
import openpyxl
from openpyxl.styles import Alignment, Font

path = r"C:\Users\SYSTE\Documents\WorkTiles改进.xlsx"
font = Font(name="等线", size=11)
center = Alignment(horizontal="center", vertical="center", wrap_text=True)
wrap = Alignment(wrap_text=True, vertical="center")

second = {
    "Sheet1": {
        10: "A",  # 选中项改成中性灰
        12: "B:这次没动视频铺满。黑边变大，还要对一下预览和真实画面是不是各裁了一次。",
        15: "B:左右分屏已经稳定。换图偶发只清空、不显示新图，这次没定位到那条路径。",
        20: "A",  # 裁切拖动不再挪画面，松手再往左上收
        21: "B:横向图片转 90 度后上下被裁、留下灰边，这次还没按转完的外框重新铺满。",
        23: "B:已排除独占全屏。窗口模式下微信输入法也不显示候选窗，微软拼音正常。还没找到微信输入法单独不画候选窗的原因。",
        24: "B:重进设置后预览只剩背景色，网页壁纸的毛玻璃这次没改。",
        25: "B:悬停和还没加载出图时仍可能显示文件名，名称这次没再改。",
        28: "B:行末那一格的字号已经对了，但光标仍能选到段落标记。还没把选区限制在最后一个字。",
    },
    "Sheet2": {
        4: "A",
        8: "A",
        9: "B:常规页、开机自启和静默启动这次仍没做。",
        10: "B:大秒数预览有时还留着小秒针，或切回小秒针后预览不显示秒。真时钟是正常的，这次没改预览刷新。",
        11: "B:托盘图标和菜单这次仍没做，还是和常规页一起。",
        12: "B:桌面磁贴的毛玻璃没有跟着圆角收边，这次没改。",
        14: "A",
        15: "B:选择框与文字同一行、下面一行色卡、克隆自定义色系，这次没做。",
        19: "A",
        20: "B:未处理异常仍只写 data/crash.log，错误窗口这次没做。",
        22: "A",
        23: "B:先不做整包搬迁。更简单的做法是根目录放启动用的 WorkTiles.exe，程序和运行库收进 app-版本，data 留在根目录。现在的便携目录先不动。",
    },
}

new_bugs = [
    ("控制窗处在右上或右下时缩放控件没了", "A"),
    ("删除磁贴时会整页刷新并卡一下。只移除被删的那一块，其它磁贴不重建。", "A"),
    ("首次启动的新建、导入和标题应在作业板上居中，而不是整个窗口", "A"),
]

wb = openpyxl.load_workbook(path)
for name, cells in second.items():
    ws = wb[name]
    for row in range(3, ws.max_row + 1):
        tested = (ws.cell(row, 4).value or "")
        tested = str(tested).strip()
        change = ws.cell(row, 5)
        follow = ws.cell(row, 6)
        if row in cells:
            change.value = cells[row]
            change.font = font
            change.alignment = wrap if str(cells[row]).startswith("B") else center
            ws.row_dimensions[row].height = 64 if str(cells[row]).startswith("B") else 32
        elif tested == "A" and not change.value:
            change.value = "\\"
            change.font = font
            change.alignment = center
            follow.value = "\\"
            follow.font = font
            follow.alignment = center

ws = wb["Sheet1"]
last = 2
for row in range(3, ws.max_row + 1):
    if ws.cell(row, 1).value:
        last = row
for offset, (problem, status) in enumerate(new_bugs):
    row = last + 1 + offset
    number = ws.cell(row, 1, f"#{row - 2}")
    number.font = font
    number.alignment = center
    problem_cell = ws.cell(row, 2, problem)
    problem_cell.font = font
    problem_cell.alignment = wrap
    status_cell = ws.cell(row, 3, status)
    status_cell.font = font
    status_cell.alignment = center
    ws.row_dimensions[row].height = 36

wb.save(path)
print("sheet1", wb["Sheet1"].max_row, "sheet2", wb["Sheet2"].max_row)
