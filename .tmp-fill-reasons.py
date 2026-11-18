# -*- coding: utf-8 -*-
import openpyxl
from openpyxl.styles import Alignment, Font

path = r"C:\Users\SYSTE\Documents\WorkTiles改进.xlsx"
font = Font(name="等线", size=11)
align = Alignment(wrap_text=True, vertical="center")

reasons = {
    "Sheet1": {
        23: "B:还没改。程序默认用独占全屏，微信输入法等候选窗很可能被系统挡住。这台机器没有装微信输入法，没法确认就是这个原因；直接改全屏方式又会动到现有全屏行为，所以先停着。",
    },
    "Sheet2": {
        9: "B:还没做。要新增“常规”页并作为打开设置时的首页，还要接开机自启和静默启动（不打开主窗口，只留托盘；桌面磁贴开着才显示磁贴）。启动流程还没改。",
        11: "B:还没做。现在没有托盘图标。菜单要按文档里的图做（重启、退出、应用设置、显示/隐藏桌面磁贴二选一、打开主界面），并且依赖上面的静默启动，所以和常规页一起还没做。",
        15: "B:还没做。主题页已经有色系和学科色卡，但选择框还没和文字放在同一行，下面也还没有一行色卡；“新建自定义色系=克隆当前色系，点色卡改色”也还没做。",
        20: "B:还没做。未处理异常现在只写进 data/crash.log，没有带重启、退出、忽略、复制错误信息的窗口。",
        23: "B:还没做。现在发布出来是一整个程序目录。WinUI 还要带一批运行库，做不到根目录只剩 WorkTiles.exe、data 和 app-版本，除非再加一层启动器并改数据目录位置。发布布局还没动。",
    },
}

wb = openpyxl.load_workbook(path)
for name, cells in reasons.items():
    ws = wb[name]
    for row, text in cells.items():
        cell = ws.cell(row, 3, text)
        cell.font = font
        cell.alignment = align
        ws.row_dimensions[row].height = 72
wb.save(path)

wb = openpyxl.load_workbook(path)
for name, cells in reasons.items():
    ws = wb[name]
    for row in cells:
        print(name, ws.cell(row, 1).value)
        print(ws.cell(row, 3).value)
        print("D =", ws.cell(row, 4).value)
        print("---")
