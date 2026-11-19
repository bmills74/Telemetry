from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, PageBreak, KeepTogether
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfbase import pdfmetrics
from reportlab.lib.colors import HexColor

OUT = r"C:\Users\Brent\source\repos\SimCoach\output\pdf\voice-commands-reference.pdf"

navy = HexColor("#18324B")
blue = HexColor("#2F6F9F")
light = HexColor("#EEF4F8")
muted = HexColor("#52616B")

styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name="Title2", parent=styles["Title"], fontName="Helvetica-Bold", fontSize=24, leading=29, textColor=navy, alignment=TA_CENTER, spaceAfter=5*mm))
styles.add(ParagraphStyle(name="Subtitle", parent=styles["Normal"], fontSize=10.5, leading=14, textColor=muted, alignment=TA_CENTER, spaceAfter=8*mm))
styles.add(ParagraphStyle(name="Section", parent=styles["Heading1"], fontName="Helvetica-Bold", fontSize=16, leading=19, textColor=navy, spaceBefore=4*mm, spaceAfter=3*mm))
styles.add(ParagraphStyle(name="Subsection", parent=styles["Heading2"], fontName="Helvetica-Bold", fontSize=11.5, leading=14, textColor=blue, spaceBefore=3*mm, spaceAfter=1.5*mm))
styles.add(ParagraphStyle(name="Body2", parent=styles["BodyText"], fontSize=9.5, leading=13, textColor=colors.black, spaceAfter=2*mm))
styles.add(ParagraphStyle(name="Command", parent=styles["BodyText"], fontName="Helvetica-Bold", fontSize=9.5, leading=12, textColor=navy))
styles.add(ParagraphStyle(name="Header", parent=styles["BodyText"], fontName="Helvetica-Bold", fontSize=9.5, leading=12, textColor=colors.white))
styles.add(ParagraphStyle(name="Note", parent=styles["BodyText"], fontSize=8.8, leading=12, textColor=muted))

def P(text, style="Body2"):
    return Paragraph(text, styles[style])

def command_table(rows):
    data = [[P("Say", "Header"), P("What it does", "Header")]]
    for command, meaning in rows:
        data.append([P(f'“{command}”'), P(meaning)])
    table = Table(data, colWidths=[72*mm, 105*mm], repeatRows=1, hAlign="LEFT")
    table.setStyle(TableStyle([
        ("BACKGROUND", (0,0), (-1,0), navy),
        ("TEXTCOLOR", (0,0), (-1,0), colors.white),
        ("VALIGN", (0,0), (-1,-1), "TOP"),
        ("GRID", (0,0), (-1,-1), 0.35, HexColor("#C9D6DF")),
        ("ROWBACKGROUNDS", (0,1), (-1,-1), [colors.white, light]),
        ("LEFTPADDING", (0,0), (-1,-1), 6),
        ("RIGHTPADDING", (0,0), (-1,-1), 6),
        ("TOPPADDING", (0,0), (-1,-1), 5),
        ("BOTTOMPADDING", (0,0), (-1,-1), 5),
    ]))
    return table

def footer(canvas, doc):
    canvas.saveState()
    canvas.setStrokeColor(HexColor("#D7E0E6"))
    canvas.line(18*mm, 14*mm, 192*mm, 14*mm)
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(muted)
    canvas.drawString(18*mm, 9*mm, "Sim Coach - voice command reference")
    canvas.drawRightString(192*mm, 9*mm, f"Page {doc.page}")
    canvas.restoreState()

story = []
story += [Spacer(1, 12*mm), P("Sim Coach", "Title2"), P("Voice commands for live coaching and recorded-lap review", "Subtitle")]
story += [P("Use the wheel PTT button, wait for the short acknowledgement tick, speak one command, then release. Commands are interpreted against the current reference lap and the most recently completed corner.", "Body2")]
story += [P("Quick start", "Section"), command_table([
    ("Help", "List the available command families."),
    ("How was that corner?", "Give a general report for the most recent completed corner."),
    ("Repeat", "Repeat the latest coaching response."),
    ("Quiet", "Mute automatic spoken coaching."),
    ("Resume coaching", "Unmute automatic coaching."),
])]

story += [P("1. Corner selection and automatic coaching", "Section"), P("These commands control which corners Auto coach is allowed to speak about. They remain active until changed.", "Body2"), command_table([
    ("Only talk about corners 1 and 3.", "Restrict automatic coaching to the named corners."),
    ("Only talk about corner 2.", "Restrict automatic coaching to one corner."),
    ("Ignore the last corner.", "Exclude the final detected corner."),
    ("Only talk about the last corner.", "Coach only the final detected corner."),
    ("All corners.", "Clear corner restrictions."),
    ("Which corners are enabled?", "Confirm the active corner filter."),
])]

story += [P("2. Persistent topic selection", "Section"), P("Choose the type of feedback you want Auto coach to prioritise. Change it whenever you are ready to move on.", "Body2"), command_table([
    ("Only discuss braking.", "Focus automatic feedback on brake points."),
    ("Only discuss line.", "Focus on entry, apex, exit, and racing-line position."),
    ("Only discuss turn-in.", "Focus on turn-in timing and steering initiation."),
    ("Only discuss throttle.", "Focus on getting back to power and exit throttle."),
    ("Discuss everything.", "Restore the full coaching report."),
    ("Only talk about corners 1 and 3, and only discuss braking.", "Set both a corner filter and a topic in one command."),
])]

story += [P("3. Feedback detail", "Section"), P("Use these after a corner has been completed. In live mode, the coach uses the latest completed corner when you do not name one.", "Body2"),
          P("Braking", "Subsection"), command_table([
    ("How was my braking?", "Compare your brake point with the reference."),
    ("How was my brake point in corner 3?", "Report the braking comparison for Corner 3."),
    ("How many metres early or late was I?", "Give the brake-point distance difference."),
    ("Tell me about the braking points.", "Summarise the reference braking points."),
]), P("Line and apex", "Subsection"), command_table([
    ("How was my line?", "Describe the main line issue."),
    ("How was my line in corner 1?", "Describe entry, apex, and exit line performance."),
    ("Was I too tight?", "Check for an inside or tight line."),
    ("Did I run wide?", "Check for an outside or wide line."),
    ("How was my apex?", "Focus on apex timing and position."),
]), P("Turn-in and steering", "Subsection"), command_table([
    ("How was my turn-in?", "Compare turn-in timing and speed."),
    ("How was my steering?", "Compare steering input and steering build."),
    ("Was I turning in too early?", "Check turn-in timing against the reference."),
]), P("Throttle and exit", "Subsection"), command_table([
    ("How was my throttle on exit?", "Compare when you reached full throttle."),
    ("Did I get back on power soon enough?", "Check exit throttle timing."),
    ("Was I too abrupt on throttle?", "Check throttle smoothness and exit traction."),
])]

story += [P("4. Time and lap-to-lap progress", "Section"), command_table([
    ("What was the time difference?", "Report the current cumulative replay-versus-reference difference."),
    ("How much time did I lose in that corner?", "Report the completed corner time loss."),
    ("Where am I losing the most time?", "Identify the biggest current opportunity."),
    ("Am I improving?", "Compare the latest result with the previous lap."),
    ("Is that corner getting worse?", "Check the lap-to-lap trend."),
])]

story += [P("5. Output controls", "Section"), P("These reduce repetition when you already know the context while driving.", "Body2"), command_table([
    ("Stop talking about time.", "Remove time-loss and improving/worsening commentary."),
    ("Include time again.", "Restore time and lap-to-lap commentary."),
    ("Stop saying corner names.", "Omit repeated corner labels from spoken reports."),
    ("Say corner names again.", "Restore corner labels."),
    ("Quiet.", "Stop automatic speech immediately and mute further automatic reports."),
])]

story += [P("6. Useful natural phrasing", "Section"), P("The parser accepts several conversational forms, but short commands are safest while driving. If a phrase is not understood, try naming the topic and corner directly, for example: “braking in corner 3” or “line in corner 1.”", "Body2"), command_table([
    ("Tell me about corner one.", "General corner report for Corner 1."),
    ("How do I do in corner two?", "General corner report for Corner 2."),
    ("Let us move on to the line.", "Switch the active topic to line feedback."),
    ("I am happy with braking; tell me about turn-in.", "Switch the active topic to turn-in feedback."),
])]

story += [Spacer(1, 5*mm), P("Tip: wait for the acknowledgement tick before speaking. Blank or near-silent recordings are ignored. AI narration may make the wording more conversational, but the measured telemetry remains the source of truth.", "Note")]

doc = SimpleDocTemplate(OUT, pagesize=A4, rightMargin=18*mm, leftMargin=18*mm, topMargin=15*mm, bottomMargin=19*mm, title="Sim Coach Voice Commands")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print(OUT)
