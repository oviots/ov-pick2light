"""Generate the one-page end-user guide. Requires reportlab and Vietnamese-capable TTF fonts."""
import argparse
from pathlib import Path
from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle

parser = argparse.ArgumentParser()
parser.add_argument('--font', required=True)
parser.add_argument('--bold-font', required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = root / 'output/pdf/HUONG_DAN_SU_DUNG_P2L.pdf'
output.parent.mkdir(parents=True, exist_ok=True)
pdfmetrics.registerFont(TTFont('Guide', args.font))
pdfmetrics.registerFont(TTFont('GuideBold', args.bold_font))
navy = colors.HexColor('#15354A')
body = ParagraphStyle('Body', fontName='Guide', fontSize=11, leading=17, textColor=navy)
heading = ParagraphStyle('Heading', parent=body, fontName='GuideBold', fontSize=16, leading=22, spaceAfter=12)
small = ParagraphStyle('Small', parent=body, fontSize=10, leading=15)
code = ParagraphStyle('Code', fontName='Courier', fontSize=10, leading=17, textColor=navy)
label = ParagraphStyle('Label', parent=body, fontName='GuideBold', fontSize=10)
width = A4[0] - 100

def commands(windows, unix):
    rows = [[Paragraph('Windows (PowerShell)', label), Paragraph('macOS / Linux (Terminal)', label)],
            [Paragraph(windows, code), Paragraph(unix, code)]]
    table = Table(rows, colWidths=[width/2, width/2])
    table.setStyle(TableStyle([
        ('BACKGROUND',(0,0),(-1,-1),colors.HexColor('#F1F5F8')),
        ('VALIGN',(0,0),(-1,-1),'TOP'),
        ('LEFTPADDING',(0,0),(-1,-1),14),('RIGHTPADDING',(0,0),(-1,-1),12),
        ('TOPPADDING',(0,0),(-1,-1),10),('BOTTOMPADDING',(0,0),(-1,-1),10),
        ('LINEBELOW',(0,0),(-1,0),.5,colors.HexColor('#DCE5EB')),
    ]))
    return table

story = [
    Paragraph('P2L', ParagraphStyle('Brand',parent=heading,fontSize=28,leading=34)),
    Paragraph('HƯỚNG DẪN SỬ DỤNG', heading),
    Paragraph('Phiên bản 1.0.3 | Kích hoạt license và chạy Terminal', small),
    Spacer(1,24),
    Paragraph('Trước khi bắt đầu', ParagraphStyle('Prep',parent=body,fontName='GuideBold',spaceAfter=6)),
    Paragraph('Giải nén gói tải về. Mở Terminal hoặc PowerShell tại thư mục vừa giải nén. Máy cần có Internet và kết nối với Controller.', body),
    Spacer(1,26),
    Paragraph('1. Kích hoạt license', heading),
    Paragraph('Chỉ thực hiện một lần trên mỗi máy.', body),Spacer(1,10),
    commands('.\\p2l.exe license activate','chmod +x ./p2l<br/>./p2l license activate'),
    Spacer(1,12),
    Paragraph('Dán mã license do quản trị cung cấp, rồi nhấn Enter. Mã sẽ không hiện trên màn hình khi nhập. Chờ thông báo kích hoạt thành công.', body),
    Spacer(1,28),
    Paragraph('2. Chạy P2L', heading),
    commands('.\\p2l.exe','./p2l'),Spacer(1,12),
    Paragraph('Giữ cửa sổ Terminal mở trong lúc làm việc. Đèn sẽ sáng khi WMS cấp phiên lấy hàng.', body),
    Spacer(1,10),
    Paragraph('Những lần sau chỉ cần chạy lại lệnh ở bước 2, không phải nhập lại license.', body),
    Spacer(1,24),
    Paragraph('Thoát: nhấn Ctrl+C. Các đèn đang chạy sẽ được tắt.', ParagraphStyle('Exit',parent=body,fontName='GuideBold')),
    Spacer(1,14),
    Paragraph('Nếu không kích hoạt hoặc kết nối được, gửi thông báo lỗi cho quản trị để được hỗ trợ.', small),
]

def footer(canvas, document):
    canvas.setStrokeColor(colors.HexColor('#DCE5EB'))
    canvas.line(50,45,A4[0]-50,45)
    canvas.setFont('Guide',9)
    canvas.setFillColor(colors.HexColor('#637887'))
    canvas.drawString(50,29,'P2L 1.0.3')
    canvas.drawRightString(A4[0]-50,29,'Hướng dẫn người dùng | 1/1')

SimpleDocTemplate(str(output),pagesize=A4,rightMargin=50,leftMargin=50,topMargin=42,bottomMargin=65,
    title='Hướng dẫn sử dụng P2L 1.0.3',author='P2L').build(story,onFirstPage=footer,onLaterPages=footer)
print(output)
