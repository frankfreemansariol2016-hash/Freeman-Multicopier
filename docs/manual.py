# -*- coding: utf-8 -*-
"""
Manual de usuario de Freeman MULTICOPIER.

Replica el sistema de diseno del PDF de referencia medido con PyMuPDF:
pagina de 540x720 pt, fondo #808080, Century Gothic, acento ambar #ffc000,
titulos de pagina a 31.6 pt, cuerpo a 14 pt con interlínea de 16.8 pt,
numeros de lista centrados en x=56.3 con el texto en x=88, y notas de
10-11 pt en ambar.
"""
import os
from reportlab.pdfgen import canvas
from reportlab.lib.colors import HexColor
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

# ----------------------------------------------------------------- medidas
W, H = 540.0, 720.0
ML, MR = 35.8, 35.8
CW = W - ML - MR
TITLE_Y = 669.8          # linea base del titulo de pagina
BOTTOM = 62.0           # no se dibuja nada por debajo de esta linea
LEAD = 16.8             # interlínea del cuerpo de 14 pt
ITEM_PITCH = 34.9       # separacion entre elementos numerados

# ----------------------------------------------------------------- color
BG     = HexColor("#808080")
DIM    = HexColor("#7c7c7c")
AMBER  = HexColor("#ffc000")
WHITE  = HexColor("#ffffff")
TITLE  = HexColor("#f8f8f8")
YELLOW = HexColor("#ffff00")
PANEL  = HexColor("#767676")
PANEL2 = HexColor("#6d6d6d")
CARD   = HexColor("#7e7e7e")
CARD2  = HexColor("#6f6f6f")
LINE   = HexColor("#9b9b9b")
REDBR  = HexColor("#ff3b30")
REDFI  = HexColor("#7d3b39")
REDFILL = HexColor("#ff8a80")

# ----------------------------------------------------------------- fuentes
pdfmetrics.registerFont(TTFont("G", "C:/Windows/Fonts/GOTHIC.TTF"))
pdfmetrics.registerFont(TTFont("GB", "C:/Windows/Fonts/GOTHICB.TTF"))

OUT = os.environ.get("MANUAL_OUT", "manual.pdf")
cv = canvas.Canvas(OUT, pagesize=(W, H))
cv.setTitle("Freeman MULTICOPIER - Manual de usuario")
cv.setAuthor("Freeman MULTICOPIER")
cv.setSubject("Manual de usuario")
cv.setKeywords("copiador, multiobjetivo, USB, Windows, espejo, sincronizacion")


def sw(t, f="G", s=14.0):
    return pdfmetrics.stringWidth(t, f, s)


def wrap(txt, font, size, width):
    """Parte un texto en lineas que caben en width. Respeta los saltos
    manuales con una barra vertical."""
    out = []
    for hard in txt.split("|"):
        cur = ""
        for word in hard.split():
            cand = (cur + " " + word).strip()
            if sw(cand, font, size) <= width or not cur:
                cur = cand
            else:
                out.append(cur)
                cur = word
        out.append(cur)
    return out


def newpage():
    cv.setFillColor(BG)
    cv.rect(0, 0, W, H, stroke=0, fill=1)


def foots():
    cv.setFillColor(HexColor("#6e6e6e"))
    cv.setFont("G", 9)
    cv.drawCentredString(W / 2.0, 30, str(cv.getPageNumber()))


class P(object):
    """Una pagina. Cada metodo baja el cursor y salta de pagina si hace
    falta, para que nunca haya texto debajo de la linea de pie."""

    def __init__(self):
        newpage()
        self.y = TITLE_Y

    def need(self, h):
        if self.y - h < BOTTOM:
            self.brk()

    def brk(self):
        foots()
        cv.showPage()
        P.__init__(self)

    def end(self):
        foots()
        cv.showPage()

    # ---------------------------------------------------------- titulo
    def title(self, txt):
        cv.setFillColor(TITLE)
        cv.setFont("G", 31.6)
        cv.drawString(ML, self.y, txt)
        self.y -= 8
        cv.setStrokeColor(HexColor("#9a9a9a"))
        cv.setLineWidth(0.7)
        cv.line(ML, self.y, W - MR, self.y)
        self.y -= 26

    # ---------------------------------------------------------- cuerpo
    def p(self, txt, size=14.0, color=WHITE, font="G", x=ML, width=None, lead=None):
        width = CW if width is None else width
        lead = LEAD if lead is None else lead
        lines = wrap(txt, font, size, width)
        self.need(lead * len(lines) + 6)
        cv.setFillColor(color)
        cv.setFont(font, size)
        for ln in lines:
            cv.drawString(x, self.y, ln)
            self.y -= lead
        self.y -= 2

    def rich(self, parts, size=14.0, x=ML, width=None, lead=None):
        """parts = [(texto, color, negrita), ...] en un solo parrafo sin
        partir palabras entre colores distintos."""
        width = CW if width is None else width
        lead = LEAD if lead is None else lead
        words = []
        for txt, col, bold in parts:
            f = "GB" if bold else "G"
            for i, wd in enumerate(txt.split()):
                if wd:
                    words.append((wd, col, f))
        lines, cur, curw = [], [], 0.0
        for wd, col, f in words:
            ww = sw(wd, f, size) + sw(" ", f, size)
            if curw + ww > width and cur:
                lines.append((cur, curw))
                cur, curw = [], 0.0
            cur.append((wd, col, f))
            curw += ww
        if cur:
            lines.append((cur, curw))
        self.need(lead * len(lines) + 6)
        for ln, _ in lines:
            px = x
            for wd, col, f in ln:
                cv.setFillColor(col)
                cv.setFont(f, size)
                cv.drawString(px, self.y, wd)
                px += sw(wd, f, size) + sw(" ", f, size)
            self.y -= lead
        self.y -= 2

    # --- vinetas
    def dot(self, x, y, col=AMBER, r=2.6):
        cv.setFillColor(col)
        cv.circle(x, y + 5.6, r, stroke=0, fill=1)

    def b(self, txt, size=14.0, indent=18.0, color=WHITE, marker="box"):
        lines = wrap(txt, "G", size, CW - indent - 8)
        self.need(LEAD * len(lines) + 12)
        y0 = self.y
        if marker == "box":
            cv.setFillColor(AMBER)
            cv.rect(ML + 2, y0 + 2.5, 6, 6, stroke=0, fill=1)
        elif marker == "check":
            cv.setStrokeColor(AMBER)
            cv.setLineWidth(1.8)
            cv.setLineCap(1)
            q = cv.beginPath()
            q.moveTo(ML + 1, y0 + 5)
            q.lineTo(ML + 4, y0 + 1.6)
            q.lineTo(ML + 9.5, y0 + 11)
            cv.drawPath(q, stroke=1, fill=0)
        elif marker == "arrow":
            cv.setFillColor(AMBER)
            q = cv.beginPath()
            q.moveTo(ML + 1, y0 + 1)
            q.lineTo(ML + 10, y0 + 5.5)
            q.lineTo(ML + 1, y0 + 10)
            q.close()
            cv.drawPath(q, stroke=0, fill=1)
        cv.setFillColor(color)
        cv.setFont("G", size)
        for ln in lines:
            cv.drawString(ML + indent, self.y, ln)
            self.y -= LEAD
        self.y -= 2.5

    # ---------------------------------------------------------- lista numerada
    def n(self, num, txt, color=WHITE):
        """Numero centrado en x=56.3 a 18 pt y texto en x=88, igual que el
        PDF de referencia."""
        lines = wrap(txt, "G", 14.0, W - MR - 88.0)
        self.need(LEAD * len(lines) + 18)
        cv.setFillColor(WHITE)
        cv.setFont("G", 18)
        cv.drawCentredString(56.3, self.y - 3.2, num)
        cv.setFillColor(color)
        cv.setFont("G", 14.0)
        for ln in lines:
            cv.drawString(88.0, self.y, ln)
            self.y -= LEAD
        self.y -= 9

    # ---------------------------------------------------------- bloques
    def h2(self, txt, color=WHITE, size=18.0):
        self.need(size + 50)
        self.y -= 8
        cv.setFillColor(color)
        cv.setFont("G", size)
        cv.drawString(ML, self.y, txt)
        self.y -= 10

    def h3(self, txt, color=AMBER):
        self.need(64)
        self.y -= 7
        cv.setFillColor(color)
        cv.rect(ML, self.y - 1, 7, 7, stroke=0, fill=1)
        cv.setFillColor(WHITE)
        cv.setFont("GB", 14)
        cv.drawString(ML + 15, self.y, txt)
        self.y -= 14

    def note(self, txt, size=10.5, color=AMBER):
        lines = wrap(txt, "G", size, CW - 12)
        self.need(13.5 * len(lines) + 14)
        self.y -= 2
        cv.setFillColor(color)
        cv.setFont("G", size)
        for ln in lines:
            cv.drawString(ML + 8, self.y, ln)
            self.y -= 13.5
        self.y -= 5

    def warn(self, txt):
        self.note(txt, size=11.0, color=YELLOW)

    def gap(self, h=10):
        self.y -= h

    def rule(self):
        self.need(20)
        cv.setStrokeColor(HexColor("#949494"))
        cv.setLineWidth(0.7)
        cv.line(ML, self.y, W - MR, self.y)
        self.y -= 16

    def table(self, head, rows, widths):
        """Tabla de dos niveles: cabecera en 18 pt y filas en 14 pt, sin
        recuadros, como las listas del manual de referencia."""
        self.need(30)
        cv.setFillColor(AMBER)
        cv.setFont("GB", 18)
        cv.drawString(ML, self.y, head)
        self.y -= 8
        cv.setStrokeColor(HexColor("#949494"))
        cv.setLineWidth(0.7)
        cv.line(ML, self.y, W - MR, self.y)
        self.y -= 20
        for a, b in rows:
            la = wrap(a, "GB", 13.0, widths[0] - 8)
            lb = wrap(b, "G", 13.0, widths[1] - 10)
            h = max(len(la), len(lb)) * 17.0
            self.need(h + 8)
            ya = self.y
            cv.setFillColor(AMBER)
            cv.setFont("GB", 13)
            for ln in la:
                cv.drawString(ML, ya, ln)
                ya -= 17
            yb = self.y
            cv.setFillColor(WHITE)
            cv.setFont("G", 13)
            for ln in lb:
                cv.drawString(ML + widths[0], yb, ln)
                yb -= 17
            self.y -= h + 4
            cv.setStrokeColor(HexColor("#8a8a8a"))
            cv.setLineWidth(0.4)
            cv.line(ML, self.y + 2, W - MR, self.y + 2)
        self.y -= 12


# ================================================================== PORTADA
newpage()
cv.setFillColor(DIM)
cv.setFont("G", 15)
cv.drawString(58.4, 430.0, "MANUAL DE USUARIO")
cv.setFillColor(TITLE)
cv.setFont("G", 40)
cv.drawString(58.4, 390.0, "Freeman")
cv.drawString(58.4, 346.0, "MULTICOPIER")
cv.setFillColor(AMBER)
cv.setFont("G", 14)
cv.drawString(58.4, 200.0, "Versión 0.1")
cv.setFillColor(WHITE)
cv.setFont("G", 12)
cv.drawString(58.4, 178.0, "Copiador multiobjetivo para Windows")
cv.setFillColor(DIM)
cv.setFont("G", 10.5)
cv.drawString(58.4, 156.0, "Lee el origen una vez y escribe a la vez en todas las")
cv.drawString(58.4, 142.0, "memorias que tengas marcadas.")
cv.drawString(58.4, 120.0, "Sin licencia. Sin límite de dispositivos.")
cv.drawString(58.4, 104.0, "Sueltas los archivos y la copia empieza sola.")
cv.showPage()

# ================================================================== APLICACION
p = P()
p.title("APLICACIÓN")

FX, FY = ML, 196.0
FW, FH = CW, 424.0
RX, RW = FX + 162.0, CW - 163.5
TB_Y, TB_H = FY + FH - 36.0, 35.0
AB_Y, AB_H = FY + 12.0, 32.0
SB_Y, SB_H = FY + 1.5, 8.0
MID_Y = AB_Y + AB_H + 4.0
MID_H = TB_Y - MID_Y - 4.0
LX, LW = FX + 1.5, 158.0
CARD_W = (RW - 6) / 2.0
CARD_H = 66.0

cv.setFillColor(PANEL)
cv.setStrokeColor(AMBER)
cv.setLineWidth(1.3)
cv.rect(FX, FY, FW, FH, stroke=1, fill=1)


def box(x, y, w, h, fill, stroke=LINE, lw=0.7):
    cv.setFillColor(fill)
    cv.setStrokeColor(stroke)
    cv.setLineWidth(lw)
    cv.rect(x, y, w, h, stroke=1, fill=1)


def tag(x, y, txt, size=18.0, color=WHITE, align="l"):
    cv.setFillColor(color)
    cv.setFont("G", size)
    w = sw(txt, "G", size)
    cv.drawString(x if align == "l" else x - w, y, txt)


def pin(n, x, y):
    """Numerito ambar que senala una zona del dibujo."""
    cv.setFillColor(AMBER)
    cv.circle(x, y + 3.4, 6.0, stroke=0, fill=1)
    cv.setFillColor(HexColor("#4a4a4a"))
    cv.setFont("GB", 8.4)
    cv.drawCentredString(x, y + 0.6, str(n))


# ------------------------------------------------------------- barra superior
box(FX + 1.5, TB_Y, FW - 3.0, TB_H, PANEL2)
tag(FX + 32, TB_Y + 21, "FREEMAN MULTICOPIER", 10.0, TITLE)
tag(FX + 32, TB_Y + 9, "v0.1", 8.0, DIM)
tag(FX + FW - 10, TB_Y + 20, "DISPOSITIVOS  SELECCIONADOS  OCUPANDO  RAM  VELOCIDAD", 7.4, AMBER, align="r")
tag(FX + FW - 10, TB_Y + 9, "13        0        0        24 MB        0 MB/s", 7.4, WHITE, align="r")

# ------------------------------------------------------------- panel izquierdo
box(LX, MID_Y, LW, MID_H, PANEL)
cv.setStrokeColor(AMBER)
cv.setLineWidth(1.0)
cv.line(LX + LW, MID_Y, LX + LW, MID_Y + MID_H)
c = MID_Y + MID_H
for t, act in (("COPIA EN CURSO", True), ("BUSCADOR", False)):
    c -= 20.0
    if act:
        box(LX + 1.0, c - 4.0, LW - 2.0, 20.0, PANEL2)
        cv.setFillColor(AMBER)
        cv.rect(LX + 1.0, c - 4.0, 3, 20, stroke=0, fill=1)
    tag(LX + 12, c, t, 9.0, TITLE if act else DIM)
# ------------------------------------- el bloque ORIGEN, arriba del panel
OG_Y, OG_H = 424.0, 104.0
box(LX + 6, OG_Y, LW - 12, OG_H, CARD)
cv.setStrokeColor(AMBER)
cv.setLineWidth(0.9)
cv.line(LX + 6, OG_Y + 2, LX + 6, OG_Y + OG_H - 2)
tag(LX + 13, OG_Y + OG_H - 11, "ORIGEN", 7.2, DIM)
tag(LX + LW - 12, OG_Y + OG_H - 11, "1.2 GB", 6.6, DIM, align="r")
tag(LX + 13, OG_Y + OG_H - 24, "peli-0.mkv", 8.0, TITLE)
tag(LX + 13, OG_Y + OG_H - 34, "D:\\Series\\Temporada 1", 6.2, DIM)
tag(LX + 13, OG_Y + OG_H - 50, "LECTURA 658 MB/s", 7.2, AMBER)
tag(LX + 13, OG_Y + OG_H - 62, "TRANSFERENCIA 1.5 GB/s hacia 4 destinos", 6.2, WHITE)
tag(LX + 13, OG_Y + OG_H - 76, "K: 402   L: 388   P: 371   M: 340 MB/s", 6.0, DIM)
tag(LX + 13, OG_Y + OG_H - 88, "ADEMAS  peli-1.mkv  657 MB/s de origen,", 6.0, AMBER)
tag(LX + 13, OG_Y + OG_H - 98, "        1.5 GB/s hacia 4 destinos", 6.0, AMBER)

CV_Y = MID_Y + 8.0
CV_H = 188.0
box(LX + 6, CV_Y, LW - 12, CV_H, CARD2)
tag(LX + 12, CV_Y + CV_H - 13, "K:   COPIANDO   62 %", 7.6, TITLE)
tag(LX + 12, CV_Y + CV_H - 25, "video_01.mp4", 6.8, AMBER)
box(LX + 12, CV_Y + CV_H - 34, LW - 30, 4, HexColor("#555555"))
box(LX + 12, CV_Y + CV_H - 34, (LW - 30) * 0.62, 4, AMBER)
box(LX + 12, CV_Y + CV_H - 44, LW - 30, 2, HexColor("#555555"))
box(LX + 12, CV_Y + CV_H - 44, (LW - 30) * 0.31, 2, HexColor("#ff6a5e"))
tag(LX + 12, CV_Y + CV_H - 57, "24 / 40      18 MB/s      00:14", 6.4, WHITE)
tag(LX + 12, CV_Y + CV_H - 68, "Libre 7.4 GB", 6.8, REDFILL)


# ------------------------------------------------------------- barra de acciones
box(FX + 1.5, AB_Y, FW - 3.0, AB_H, PANEL2)
ax = FX + 8
for i, (t, c_) in enumerate((("MODO", AMBER), ("ESTRUCTURA", AMBER), ("COPIAR", TITLE),
                             ("Pausar todo", WHITE), ("Continuar todo", WHITE),
                             ("Seleccionar todo", WHITE), ("Quitar", WHITE),
                             ("Clonar lista", WHITE), ("Limpiar", WHITE),
                             ("Reintentar", WHITE), ("Expulsar", WHITE))):
    w = sw(t, "G", 8.0) + 12
    if ax + w > FX + FW - 34:
        break
    box(ax, AB_Y + 9, w, 15, HexColor("#3f5a3f") if i == 2 else HexColor("#5f5f5f"))
    tag(ax + 6, AB_Y + 13.5, t, 8.0, c_)
    ax += w + 4
box(ax, AB_Y + 9, 20, 15, HexColor("#5f5f5f"))
tag(ax + 6, AB_Y + 13.5, "...", 8.0, WHITE)

# ------------------------------------------------------------- barra de estado
box(FX + 1.5, SB_Y, FW - 3.0, SB_H, HexColor("#6a6a6a"))
tag(FX + 8, SB_Y + 1.5, "40 archivo(s) en la cola de 2 dispositivo(s).", 6.6, DIM)
tag(FX + FW - 8, SB_Y + 1.5, "2 de 13 seleccionados", 6.6, DIM, align="r")

# ------------------------------------------------------------- tarjetas
for i in range(4):
    cx = RX + (i % 2) * (CARD_W + 6)
    cy = MID_Y + MID_H - 18 - CARD_H - (i // 2) * (CARD_H + 6)
    sel = (i == 1)
    if sel:
        box(cx - 4, cy - 4, CARD_W + 8, CARD_H + 8, HexColor("#6b3230"))
        box(cx, cy, CARD_W, CARD_H, HexColor("#6b3a39"), REDBR, 2.2)
        cv.setFillColor(REDFI)
        cv.circle(cx + 9, cy + CARD_H - 9, 6.0, stroke=0, fill=1)
        cv.setStrokeColor(YELLOW)
        cv.setLineWidth(1.4)
        q = cv.beginPath()
        q.moveTo(cx + 6.0, cy + CARD_H - 9.2)
        q.lineTo(cx + 8.4, cy + CARD_H - 11.6)
        q.lineTo(cx + 12.2, cy + CARD_H - 6.2)
        cv.drawPath(q, stroke=1, fill=0)
    else:
        box(cx, cy, CARD_W, CARD_H, CARD)
    box(cx + 7, cy + CARD_H - 26, 17, 19, PANEL2)
    tag(cx + 29, cy + CARD_H - 12, ("K: KINGSTON", "M: MULTICOPIER 2", "P: PEN 32GB", "L: LIBRERIA")[i], 7.6, TITLE)
    tag(cx + 29, cy + CARD_H - 21, ("MEMORIA FLASH", "MEMORIA FLASH", "MEMORIA FLASH", "DISCO EXTERNO")[i], 6.0, DIM)
    tag(cx + CARD_W - 6, cy + CARD_H - 21, ("14.2 GB", "28.0 GB", "29.1 GB", "480 GB")[i], 6.0, AMBER, align="r")
    box(cx + 7, cy + 26, CARD_W - 14, 5, HexColor("#5a5a5a"))
    pr = (0.62, 1.0, 0.0, 0.0)[i]
    if pr > 0:
        box(cx + 7, cy + 26, (CARD_W - 14) * pr, 5, HexColor("#ff6a5e") if sel else AMBER)
    tag(cx + CARD_W - 6, cy + 33, ("62 %", "100 %", "--", "--")[i], 6.4, WHITE, align="r")
    tag(cx + 7, cy + 16, ("COPIANDO   18 MB/s", "TERMINADO", "EN ESPERA", "EN ESPERA")[i], 6.4, AMBER if i == 1 else WHITE)
    tag(cx + 7, cy + 7, "Libre 7.4 GB       24 / 40 archivos", 6.2, DIM)

# ------------------------------------------------------------- leyenda
LEGENDA = [
    (1, "ICONO Y TÍTULO"),
    (2, "CONTADORES Y VELOCIDAD"),
    (3, "PESTAÑAS DEL PANEL IZQUIERDO"),
    (4, "LISTADO DE DISPOSITIVOS"),
    (5, "DISPOSITIVO MARCADO"),
    (6, "AVANCE Y PORCENTAJE"),
    (7, "ESPACIO LIBRE"),
    (8, "BARRA DE ACCIONES Y DE ESTADO"),
    (9, "EL BLOQUE ORIGEN"),
]
ly = 404.0
for n, txt in LEGENDA:
    pin(n, RX + 6, ly - 3.0)
    tag(RX + 18, ly, txt, 10.0, WHITE)
    ly -= 18.0

# ------------------------------------------------------------- puntitos
pin(1, FX + 15, TB_Y + 21.0)
pin(2, 206.0, TB_Y + 15.0)
pin(3, LX + LW - 12, MID_Y + MID_H - 30.0)
pin(4, RX + 222, MID_Y + MID_H - 8.0)
pin(5, RX + CARD_W - 12, 506.0)
pin(6, RX + CARD_W - 16, 519.0)
pin(7, LX + 64, CV_Y + CV_H - 68.0)
pin(8, 300.0, SB_Y + 4.0)
pin(9, LX + LW - 12, OG_Y + OG_H - 30.0)

p.y = FY - 24
p.h3("LAS CUATRO ZONAS")
p.b("La barra de arriba lleva el nombre y la versión a la izquierda, y a la derecha cinco datos: cuántos dispositivos hay, cuántos están marcados, cuántos están trabajando, cuánta memoria está usando el motor de copia y la velocidad total de este momento.")
p.b("El panel de la izquierda tiene dos pestañas. BUSCADOR sirve para encontrar un archivo por su nombre. COPIA EN CURSO sirve para ver, en vivo, qué se está leyendo del origen y cuánto queda libre en cada destino.")
p.b("El panel de la derecha es el listado de dispositivos. Cada tarjeta dice qué es, de qué tipo es, qué letra tiene, cuánto lleva copiado, cuánto le queda libre y en qué estado está.")
p.b("Debajo del listado está la barra de acciones: en ella se elige el modo, se elige la estructura y se arranca la copia.")
p.b("La última fila, de una sola línea, es la barra de estado: escribe en palabras lo que está pasando y resume la selección actual.")
p.h3("EL BLOQUE ORIGEN")
p.b("Arriba del panel izquierdo, con el rótulo ORIGEN, hay un bloque que dice qué archivo se está leyendo del origen ahora mismo, a qué velocidad sale, cuánto se está escribiendo en total y cuánto va a cada memoria. Está arriba porque es la primera pregunta que uno se hace: de dónde me está viniendo esto y a qué velocidad.")
p.b("Debajo de él hay una tarjeta por cada memoria que está copiando, con su avance y su espacio libre.")
p.note("Cuando el mismo archivo va a varias memorias, el bloque ORIGEN no repite la lectura: la dice una sola vez y reparte la velocidad entre los destinos. Ver el capítulo EL BLOQUE ORIGEN.")
p.end()

# ================================================================== INDICE
p = P()
p.title("ÍNDICE")
IDX = [
    (1, "RESUMEN"),
    (2, "APLICACIÓN"),
    (1, "INSTALACIÓN"),
    (1, "PRIMER USO"),
    (1, "DISPOSITIVOS"),
    (1, "MODOS DE COPIA"),
    (1, "ESTRUCTURAS DE CARPETAS"),
    (1, "CÓMO SE COPIA"),
    (1, "EL BUSCADOR"),
    (1, "COPIA EN CURSO"),
    (1, "EL BLOQUE ORIGEN"),
    (1, "LA BARRA DE ACCIONES"),
    (1, "FUNCIONES BÁSICAS"),
    (1, "PAUSA Y CONTINUAR"),
    (1, "VENTANAS"),
    (1, "CONFIGURACIÓN"),
    (1, "PREGUNTAS FRECUENTES"),
    (1, "LO QUE NO HACE ESTA VERSIÓN"),
    (1, "EL REGISTRO"),
]
for lvl, s in IDX:
    p.need(48)
    x = ML if lvl == 1 else ML + 36
    p.dot(x, p.y, WHITE if lvl == 1 else AMBER, 2.8)
    tag(x + 16, p.y, s, 18.0, AMBER)
    p.y -= 28.5
p.end()

# ================================================================== RESUMEN
p = P()
p.title("RESUMEN")
p.p("Freeman MULTICOPIER copia información a muchos dispositivos al mismo tiempo. Se marca una memoria, se sueltan encima los archivos o carpetas que quieres clonar, y el programa se encarga de escribir lo mismo en todas las memorias marcadas, leyendo el origen una sola vez.")
p.p("No hay que pulsar ningún botón para que la copia empiece: en cuanto sueltas los archivos, la copia ya está en marcha.")
p.p("Esta primera versión no hace nada más de lo que hace falta para eso. No hay licencia, no hay límite de dispositivos y no hay ninguna función que necesite conexión a internet.")
p.h3("LO QUE RESUELVE")
p.b("Lee el origen una vez y reparte el resultado en todos los destinos a la vez, en vez de repetir la lectura por cada memoria.")
p.b("Empieza a copiar en cuanto sueltas los archivos. No hay ningún botón que se pueda olvidar.")
p.b("Muestra, mientras copia, qué archivo se está leyendo del origen, a qué velocidad sale y cuánto se está escribiendo en cada destino.")
p.b("Tiene cuatro modos de copia: uno copia y respeta lo que ya hay, y los otros tres limpian el destino para dejarlo igual que el origen.")
p.b("Tiene ocho estructuras de carpetas distintas para decidir cómo queda repartido el contenido dentro de la memoria.")
p.b("El buscador localiza un archivo por su nombre sin que sepas en qué carpeta está, y lo manda directo a los dispositivos marcados.")
p.b("Se puede pausar y continuar sin perder el trabajo ya hecho.")
p.h3("LO QUE NO ES")
p.p("No formatea discos, no cambia letras de unidad, no copia por red y no indexa el contenido. La lista completa está en el capítulo LO QUE NO HACE ESTA VERSIÓN.")
p.h3("LO QUE NECESITAS")
p.b("Windows 10 o Windows 11, de 64 bits.")
p.b("Unos 60 MB libres en el disco donde se instala.")
p.b("Permisos para escribir en los destinos. Si una memoria está protegida con contraseña, Windows la pedirá.")
p.end()

# ================================================================== INSTALACION
p = P()
p.title("INSTALACIÓN")
p.p("El instalador es un único archivo. No necesita internet.")
p.n("1", "Haz doble clic en FreemanMultiCopier_Setup_0.1.0.exe.")
p.n("2", "Si Windows avisa de que el editor no es conocido, pulsa Más información y luego Ejecutar de todas formas. Aparece porque el instalador no está firmado digitalmente.")
p.n("3", "Elige la carpeta. Por defecto va dentro de Archivos de programa, en la carpeta del usuario.")
p.n("4", "Pulsa Instalar y espera. El programa ocupa unos 55 MB.")
p.n("5", "Al terminar se crea un acceso directo en el Escritorio y en el menú Inicio.")
p.note("Para desinstalar: Panel de control > Programas > Freeman MULTICOPIER > Desinstalar. No deja archivos sueltos.")
p.h3("QUÉ ESCRIBE FUERA DE SU CARPETA")
p.b("El registro, en %LOCALAPPDATA%\\FreemanMultiCopier\\registro.log")
p.b("El informe de la prueba automática, en %LOCALAPPDATA%\\FreemanMultiCopier\\prueba.txt")
p.note("Borrar esas dos carpetas no afecta al programa. Se vuelven a crear solas.")
p.h3("LA PRUEBA AUTOMÁTICA")
p.p("El programa trae 82 comprobaciones que trabajan sobre el disco, en una carpeta temporal que se borra al terminar:")
p.b('Abre una consola de PowerShell y ejecuta:  & "C:\\Program Files\\Freeman MULTICOPIER\\FreemanMultiCopier.exe" --selftest', marker="arrow")
p.b("Después abre %LOCALAPPDATA%\\FreemanMultiCopier\\prueba.txt para ver el resultado línea por línea.")
p.note("La prueba es segura: trabaja dentro de %TEMP% y no toca ningún archivo tuyo. Aun así, ciérrala antes de ejecutarla si estás copiando algo.")
p.end()

# ================================================================== PRIMER USO
p = P()
p.title("PRIMER USO")
p.n("1", "Enchufa las memorias y espera a que aparezcan en el panel de la derecha. Aparecen solas, sin que tengas que refrescar nada.")
p.n("2", "Pulsa cada tarjeta para marcarlas. Se pondrán con el marco rojo.")
p.n("3", "Comprueba que el modo sea NORMAL y que la estructura sea la que quieras.")
p.n("4", "Arrastra los archivos o las carpetas desde el Explorador de Windows hasta la ventana, por encima del listado de dispositivos.")
p.n("5", "Suelta. Y ya está: la copia empieza sola, sin pulsar nada.")
p.n("6", "Sigue el avance en el bloque ORIGEN, en la pestaña COPIA EN CURSO y en la barra de estado.")
p.warn("El paso 3 es el único que hay que recordar. Si dejas el modo en Espejo o en Eliminar Diferencias, la copia borra del destino lo que no esté en el origen. Por seguridad, el modo vuelve solo a NORMAL después de cada arrastre.")
p.h3("SI AL SOLTAR NO PASA NADA")
p.b("Comprueba que hay alguna tarjeta marcada. Si no hay ninguna marcada, el programa copia solo al dispositivo sobre el que soltaste.")
p.b("Si no hay ninguna marcada y sueltas fuera de una tarjeta, no hay a quién copiar, y el programa lo dice en la barra de estado.")
p.b("Comprueba que el arrastre viene del Explorador de Windows y no de otra aplicación.")
p.b("Mira la barra de estado: si hay un mensaje, está escrito ahí.")
p.h3("CUÁNDO SÍ HAY QUE PULSAR COPIAR")
p.b("Para arrastrar archivos, nunca: al soltar, ya copia.")
p.b("Sí hace falta cuando la cola se llenó con el buscador sin soltar nada encima de las tarjetas, y también después de Reintentar, Clonar lista o Limpiar, para arrancar otra vez lo que queda.")
p.note("Clonar lista tampoco copia: solo deja la cola preparada en los otros dispositivos. Para arrancarla hay que pulsar COPIAR.")
p.end()

# ================================================================== DISPOSITIVOS
p = P()
p.title("DISPOSITIVOS")
p.p("Cada memoria o cada disco aparece como una tarjeta. Las tarjetas se colocan solas en rejilla, de arriba abajo y de izquierda a derecha.")
p.h3("QUÉ DICE CADA TARJETA")
p.b("El icono de la izquierda, que cambia según el tipo, y el nombre de la memoria tal y como lo puso quien la formateó.")
p.b("Debajo del nombre, el tipo: por ejemplo MEMORIA FLASH o DISCO EXTERNO.")
p.b("En la esquina derecha, el espacio total de la memoria, y la barra de progreso con el porcentaje encima.")
p.b("El estado, con su color: EN ESPERA, COPIANDO, PAUSADO, TERMINADO, TERMINADO CON ERRORES, CANCELADO o FALLIDO.")
p.b("La línea de abajo: el espacio libre y cuántos archivos lleva copiados de cuántos hay en cola.")
p.h3("CÓMO SE MARCA UN DISPOSITIVO")
p.p("Con un clic en la tarjeta. Al marcarse pasan cuatro cosas a la vez:")
p.b("El marco se pone rojo.", marker="check")
p.b("El fondo de la tarjeta se tiñe ligeramente de rojo.", marker="check")
p.b("Aparece un resplandor rojo alrededor de la tarjeta.", marker="check")
p.b("Sale un distintivo redondo con un visto bueno en la esquina superior izquierda.", marker="check")
p.note("El marco mide 3 píxeles esté marcado o no, para que la rejilla no dé ningún salto al pasar de una memoria a quince.")
p.h3("LA BARRA ROJA")
p.p("La barra roja es la única forma de saber, de un vistazo, a quién se le está copiando. Conviene marcar y desmarcar antes de pulsar COPIAR, porque en modo Espejo o Eliminar Diferencias lo marcado es justo lo que se va a borrar.")
p.h3("LOS TIPOS QUE DISTINGUE")
p.b("MEMORIA FLASH: pendrive, tarjeta de memoria puesta en un lector.")
p.b("DISCO EXTERNO: disco duro o SSD de escritorio o portátil.")
p.b("DISCO INTERNO: un disco fijo del equipo.")
p.b("DISCO DE ARRANQUE: donde está Windows. Por defecto queda excluido de los arrastres, porque borrarlo deja el equipo sin arrancar.")
p.b("CARPETA DE RED: unidad mapeada. Se ve en el listado, pero todavía no se copia.")
p.b("LECTOR y ESCRITOR: cuando el mismo hueco sirve para leer de una unidad y escribir en otra.")
p.end()

# ================================================================== MODOS
p = P()
p.title("MODOS DE COPIA")
p.p("El modo decide qué pasa con lo que YA está en la memoria. Es el ajuste más importante del programa, y el que hay que mirar antes de empezar.")
p.n("1", "NORMAL. Copia lo que sueltas y no toca nada de lo que ya había. Si el archivo ya existe y es idéntico, se salta. Es el modo de siempre, y al que vuelve el programa solo tras cada arrastre.")
p.n("2", "ELIMINAR DIFERENCIAS. No copia nada. Recorre el destino y borra todo lo que no esté en el origen, y quita las carpetas que queden vacías. Es destructivo.")
p.n("3", "ESPEJO. Primero borra lo que sobra y después copia lo que falta. Al terminar, el destino queda idéntico al origen. También destructivo.")
p.n("4", "SINCRONIZACIÓN. Como Espejo, pero si el mismo archivo está en dos carpetas del destino, lo mueve en vez de borrarlo y recopiarlo. Ahorra escrituras.")
p.h3("CÓMO SE CAMBIA")
p.p("Con el botón MODO, en la barra de acciones. Cada clic avanza al siguiente modo, y el botón derecho vuelve al anterior.")
p.warn("Los tres modos destructivos piden confirmación antes de empezar. Si cancelas, no se toca nada.")
p.h3("LO QUE PUEDE IR MAL Y QUÉ HACE EL PROGRAMA")
p.b("Si una memoria se llena, ese destino se para y se marca como FALLIDO. Los demás siguen.")
p.b("Si un archivo está bloqueado, se anota el error y se sigue con el siguiente. Luego pulsa Reintentar.")
p.b("Si apagas el equipo en mitad de una copia en Espejo, la memoria puede quedar a medias. No lo elijas la primera vez con una memoria importante.")
p.end()

# ================================================================== ESTRUCTURAS
p = P()
p.title("ESTRUCTURAS")
p.p("La estructura decide cómo queda repartido el contenido dentro de la memoria. Se cambia con el botón ESTRUCTURA, igual que el modo: clic avanza, clic derecho retrocede.")
p.n("1", "ESTRUCTURA. Reproduce el árbol entero desde la raíz del origen. Si sueltas D:\\Videos\\Peli, en la memoria aparece Videos\\Peli.")
p.n("2", "CARPETAS. Copia la carpeta que soltaste con lo que lleva dentro, pero sin las carpetas de encima. Si sueltas D:\\Videos\\Peli, aparece Peli.")
p.n("3", "FICHEROS. Todo suelto en la raíz de la memoria, sin ninguna carpeta. Es la que se usa para preparar una memoria con una lista de capítulos.")
p.n("4", "CARPETA PERSONALIZADA. Todo dentro de una carpeta con el nombre que elijas. Por dentro se comporta como Carpetas.")
p.n("5", "SIN ESTRUCTURA. Decide solo: si sueltas carpetas, se comporta como Carpetas; si sueltas archivos sueltos, van a la raíz.")
p.n("6", "CARPETA SUPERIOR. Como Carpetas, pero añade una carpeta más por encima, la que contenía a la que soltaste. Si sueltas D:\\Videos\\Peli, aparece Videos\\Peli.")
p.n("7", "DIRECTORIOS VACÍOS. Crea el árbol de carpetas pero no copia ningún archivo. Sirve para dejar la memoria con la misma estructura vacía que otra.")
p.n("8", "ESTRUCTURA CON FICHEROS DE 0 KB. Crea las carpetas y, dentro, un archivo vacío con el nombre y la extensión de cada archivo. Sirve de catálogo: ves la estructura completa sin gastar espacio.")
p.h3("LA ESTRUCTURA DE PARTIDA DE CADA TIPO")
p.b("Cada dispositivo trae una estructura de partida que depende de qué es, y se cambia cuando quieras con el botón ESTRUCTURA.")
p.b("En una memoria USB o una tarjeta de memoria, la de partida es CARPETAS: sueltas una carpeta y se copia esa carpeta con lo que lleva dentro, sin las carpetas de encima.")
p.b("En un disco, la de partida es ESTRUCTURA: se reproduce el árbol entero desde la raíz de la unidad, incluida la letra de la unidad. Si sueltas D:\\Videos\\Peli, en el disco aparece la ruta entera desde la raíz, con la carpeta de la unidad arriba del todo.")
p.note("Por eso una imagen ISO suelta sobre un disco acaba dentro de una carpeta con la letra de la unidad, y sobre una memoria USB no. Es a propósito: en un disco lo que se replican son árboles de carpetas.")
p.h3("LO NORMAL Y LO EXCEPCIONAL")
p.b("Lo normal: la de partida del dispositivo. CARPETAS en una memoria, ESTRUCTURA en un disco.")
p.b("Lo excepcional: FICHEROS, cuando la memoria guarda capítulos que no van en carpeta, o cuando la vas a usar en un reproductor que no entiende de carpetas.")
p.b("Para hacer un catálogo sin gastar espacio: ESTRUCTURA CON FICHEROS DE 0 KB.")
p.note("La estructura se aplica a todos los dispositivos marcados a la vez. Si necesitas estructuras distintas, marca de uno en uno y copia por separado.")
p.end()

# ================================================================== COMO SE COPIA
p = P()
p.title("CÓMO SE COPIA")
p.h3("SOLTAR ES COPIAR")
p.b("Al soltar los archivos, la copia empieza sola. No hay que bajar a la barra de acciones ni que pulsar COPIAR.")
p.b("El programa suelta el ratón en cuanto ha recogido lo que arrastrabas, y sigue trabajando con la ventana en segundo plano. Se pueden seguir moviendo archivos mientras copia.")
p.b("Al terminar, el modo de copia vuelve solo a NORMAL y la barra de estado dice cuántos archivos han salido enteros, cuántos con problemas y cuántos sin tocar.")
p.h3("DÓNDE VA LO QUE SUELTAS")
p.b("Si hay tarjetas marcadas, lo que sueltes va a todas ellas: preparas quince memorias iguales en un arrastre.")
p.b("Si no hay ninguna tarjeta marcada, va solo al dispositivo sobre el que soltaste.")
p.b("Si no hay ninguna marcada y sueltas fuera de una tarjeta, el programa no hace nada y lo dice en la barra de estado.")
p.h3("LOS DOS TIPOS DE ARRASTRE")
p.b("Con el botón izquierdo se copia respetando la estructura elegida. Es el arrastre normal.")
p.b("Con el botón derecho, todo va suelto en la raíz, sin crear ninguna carpeta.")
p.note("Las dos formas de arrastrar hacen lo mismo que elegir la estructura FICHEROS, pero solo para ese arrastre y sin tocar el botón.")
p.h3("LECTURA UNA VEZ, ESCRITURA N")
p.p("El motor no lee el archivo una vez por cada memoria. Lee el origen una sola vez, lo parte en bloques, y cada bloque se escribe en todos los destinos a la vez. En quince memorias, la lectura se divide por quince.")
p.b("Los bloques se piden y se devuelven con una reserva de memoria reutilizable, para no crear buffers sin parar.")
p.b("El paso de bloques está limitado: el motor no se adelanta kilómetros. Si un destino va lento, los demás esperan en vez de acumular memoria.")
p.b("El reparto se ajusta solo: si un destino se atasca, el motor le da menos turno y aprovecha más el de los rápidos.")
p.h3("PAUSA")
p.p("PAUSAR TODO detiene los trabajos sin cerrar nada a medias. Al pulsar CONTINUAR TODO siguen desde el punto exacto, sin repetir lo ya copiado ni perder la posición en la cola.")
p.end()

# ================================================================== BUSCADOR
p = P()
p.title("EL BUSCADOR")
p.p("Sirve para encontrar un archivo por su nombre cuando no sabes en qué carpeta está. Es el camino corto para preparar una lista sin navegar carpeta por carpeta.")
p.h3("CÓMO SE USA")
p.n("1", "Abre la pestaña BUSCADOR, en el panel de la izquierda.")
p.n("2", "Escribe el nombre en el campo de texto. Se busca mientras tecleas, no hace falta darle a ningún botón.")
p.n("3", "Mira la carpeta desde la que se está buscando, escrita justo debajo del campo. Si no es la que quieres, pulsa los tres puntos y elige otra.")
p.n("4", "Los resultados salen en la lista de abajo, con el tipo y el tamaño.")
p.n("5", "Puedes seleccionar varios resultados a la vez, con Ctrl y con Mayús.")
p.n("6", "Sueltalos encima de las tarjetas marcadas, o pulsa COPIAR si ya estaban en la cola.")
p.note("La búsqueda es por subcadena, no exacta. Escribe un trozo del nombre y encontrará todo lo que lo contenga. La carpeta de búsqueda se recuerda entre sesiones.")
p.h3("LÍMITES")
p.b("Solo mira dentro de la carpeta elegida. No busca en todo el disco.")
p.b("No lee el contenido de los archivos: busca por nombre, no por lo que tienen dentro.")
p.b("Con muchísimos archivos en la carpeta, la lista tarda un poco más en aparecer. El programa va rellenándola a medida que encuentra, para que puedas empezar a elegir antes de que termine.")
p.end()

# ================================================================== COPIA EN CURSO
p = P()
p.title("COPIA EN CURSO")
p.p("Esta pestaña responde a una pregunta: qué se está leyendo del origen ahora mismo y cuánto queda libre en cada destino. Se refresca cada 1 a 3 milisegundos, según cómo lo tengas configurado.")
p.h3("QUÉ MUESTRA CADA TARJETA")
p.b("El nombre del destino.")
p.b("El porcentaje de avance del trabajo completo de ese destino.")
p.b("Debajo, el nombre del archivo que se está leyendo del origen en este momento. Es el dato que sirve para saber si la copia va bien de verdad o solo parece que avanza.")
p.b("La barra gruesa: el progreso de todo el trabajo de ese destino.")
p.b("La barra fina, en rojo, que solo aparece mientras hay un archivo en marcha: el avance de ese archivo en concreto. Con archivos grandes se ve moverse.")
p.b("La fila de abajo: el estado, cuántos archivos lleva de cuántos, la velocidad de ese destino, cuánto tiempo queda estimado y, en rojo, cuánto espacio libre le queda.")
p.h3("EL ESPACIO LIBRE EN VIVO")
p.p("El espacio libre no se lee del disco cada vez, porque eso es lento. Al empezar el trabajo el programa apunta cuánto había, y de ahí va restando los bytes que realmente escribe. Así la cifra que ves baja a partir del momento en que el archivo toca el disco, no cuando se acaba.")
p.note("Por eso en modo Espejo el espacio libre puede subir: al borrar lo que sobra se libera sitio de verdad. Y por eso los archivos que se saltan porque ya eran idénticos no consumen espacio.")
p.h3("EL TIEMPO QUE QUEDA")
p.p("Es una estimación a partir de la velocidad media de lo que lleva copiado. En un dispositivo lento y cambiante se aparta del tiempo real, así que conviene mirarlo como orientación y no como reloj.")
p.end()

# ============================================================ EL BLOQUE ORIGEN
p = P()
p.title("EL BLOQUE ORIGEN")
p.p("Es el bloque de arriba del panel izquierdo, con el rótulo ORIGEN. Responde a la pregunta que uno se hace primero al ver una copia en marcha: de dónde me está viniendo esto y a qué velocidad. Se refresca con el resto del panel, así que va a 1 a 3 milisegundos.")

BG_Y, BG_H = 428.0, 170.0
box(ML, BG_Y, CW, BG_H, CARD)
cv.setStrokeColor(AMBER)
cv.setLineWidth(1.2)
cv.line(ML, BG_Y + 2, ML, BG_Y + BG_H - 2)
tag(ML + 14, BG_Y + 150, "ORIGEN", 9.0, DIM)
tag(ML + CW - 12, BG_Y + 150, "1.2 GB", 8.0, DIM, align="r")
tag(ML + 14, BG_Y + 126, "peli-0.mkv", 15.0, TITLE)
tag(ML + 14, BG_Y + 108, "D:\\Series\\Temporada 1\\peli-0.mkv", 8.0, DIM)
tag(ML + 14, BG_Y + 78, "LECTURA 658 MB/s", 14.0, AMBER)
tag(ML + CW - 12, BG_Y + 78, "TRANSFERENCIA 1.5 GB/s hacia 4 destinos", 10.0, WHITE, align="r")
tag(ML + 14, BG_Y + 58, "K: 402 MB/s        L: 388 MB/s        P: 371 MB/s        M: 340 MB/s", 9.0, WHITE)
cv.setStrokeColor(LINE)
cv.setLineWidth(0.4)
cv.line(ML + 14, BG_Y + 44, ML + CW - 14, BG_Y + 44)
tag(ML + 14, BG_Y + 30, "ADEMAS  peli-1.mkv  657 MB/s de origen, 1.5 GB/s hacia 4 destinos", 9.0, AMBER)
tag(ML + 14, BG_Y + 16, "         peli-2.mkv  655 MB/s de origen, 1.4 GB/s hacia 3 destinos", 9.0, AMBER)

p.y = BG_Y - 28
p.h3("QUÉ DICEN LAS DOS LÍNEAS GRANDES")
p.b("LECTURA es la velocidad a la que el origen está soltando los bytes de ese archivo. Es la misma para todas las memorias que lo reciben, porque el origen se lee una sola vez y el resultado se reparte.")
p.b("TRANSFERENCIA es lo que se está escribiendo ahora mismo en todas las memorias sumadas, y hacia cuántos destinos va. Sube por encima de la lectura, y esa diferencia es trabajo que hacen las memorias al mismo tiempo.")
p.b("Debajo, una línea por memoria con lo que le toca a cada una. Una puede ir a 402 MB/s y otra a 340 MB/s en el mismo instante: el motor reparte el turno a quien lo aguanta mejor.")
p.h3("LA LÍNEA ADEMAS")
p.p("Cuando hay más de un archivo saliendo del origen a la vez, el que más está copiando se enseña arriba con todo detalle, y los demás aparecen resumidos en la línea ADEMAS: nombre, a qué velocidad salen del origen y cuánto se está escribiendo con ellos.")
p.note("La línea ADEMAS solo aparece cuando hay más de un archivo en marcha, que es lo normal al copiar una carpeta con muchos archivos.")
p.h3("CUANDO NO HAY NADA EN MARCHA")
p.p("El bloque entero se esconde mientras no sale nada del origen. No ocupa sitio ni distrae, y vuelve a aparecer en el primer bloque de datos.")
p.end()

# ================================================================== BARRA
p = P()
p.title("LA BARRA DE ACCIONES")
p.h3("MODO")
p.p("Avanza entre los cuatro modos. El clic derecho vuelve al anterior. Después de cada arrastre se pone solo en Normal.")
p.h3("ESTRUCTURA")
p.p("Avanza entre las ocho estructuras. El clic derecho vuelve a la anterior.")
p.h3("COPIAR")
p.p("Arranca el trabajo. Solo se activa con algo en la cola y algún dispositivo marcado. Lo ya terminado no se repite.")
p.note("Soltar archivos sobre la ventana ya arranca la copia por su cuenta. COPIAR está para lo que llega a la cola sin soltar nada encima: el buscador, Reintentar, Clonar lista o Limpiar.")
p.h3("Pausar todo / Continuar todo")
p.p("Detiene y reanuda todos los trabajos a la vez.")
p.h3("Seleccionar todo / Quitar")
p.p("Marca o desmarca todas las tarjetas de golpe.")
p.h3("Clonar lista")
p.p("Mete en los otros dispositivos la misma cola de archivos que ya tienes en uno, sin volver a buscarlos. Sirve para replicar un trabajo en una memoria enchufada tarde.")
p.note("Clonar lista no empieza a copiar. Solo deja la cola preparada.")
p.h3("Limpiar")
p.p("Saca de la cola lo ya copiado bien, dejando solo lo que falta.")
p.h3("Reintentar")
p.p("Vuelve a encolar solo los archivos que fallaron, para intentar otra vez sin repetir los que ya salieron.")
p.h3("Expulsar")
p.p("Saca con seguridad los dispositivos marcados, esperando a que termine lo pendiente para no dejar archivos a medias.")
p.note("Expulsar solo trabaja con dispositivos USB: en un disco interno no hace nada.")
p.end()

# ================================================================== BASICAS
p = P()
p.title("FUNCIONES BÁSICAS")
p.n("1", "Marca las memorias con un clic. El marco rojo indica quién va a recibir.")
p.n("2", "Arrastra con el botón izquierdo para copiar con la estructura elegida. Al soltar, empieza.")
p.n("3", "Arrastra con el botón derecho para soltar todo en la raíz, sin carpetas. Al soltar, empieza.")
p.n("4", "Cambia el modo con el botón, y vuelve a Normal solo en cuanto sueltas algo. Es la protección principal contra un borrado descuidado.")
p.n("5", "Mira el bloque ORIGEN: qué archivo va del origen, a qué velocidad y cuánto sale hacia cada memoria.")
p.n("6", "Mira la pestaña COPIA EN CURSO: el avance de cada destino y cuánto libre le queda.")
p.n("7", "Si un destino se pone lento, pulsa Pausar todo, y Continuar todo cuando se haya destrabado.")
p.n("8", "Si un destino falla, pulsa Reintentar para volver a intentarlo solo con los que fallaron.")
p.n("9", "Pulsa Limpiar entre tandas, para no arrastrar una cola de mil archivos ya copiados.")
p.n("10", "Usa Clonar lista para replicar en una memoria que has enchufado tarde, y pulsa COPIAR para arrancarlo.")
p.n("11", "Expulsa al final, con seguridad.")
p.note("El orden natural es: marcar, soltar, mirar y expulsar. Todo lo demás es para cuando algo se tuerce.")
p.end()

# ================================================================== PAUSA
p = P()
p.title("PAUSA Y CONTINUAR")
p.p("Pausar detiene el trabajo sin cerrar archivos a medias ni perder la cola. Continuar lo reanuda desde el punto exacto.")
p.b("Pausar todo detiene a la vez todos los trabajos en marcha.")
p.b("Continuar todo los reanuda todos.")
p.b("Al pausar, cada destino pasa a estado PAUSADO y su barra se queda congelada en el porcentaje que tenía.")
p.b("Lo ya copiado no se vuelve a copiar. El motor se acuerda de por dónde iba dentro de cada archivo.")
p.b("Puedes pausar, desconectar una memoria, y al volver a enchufarla seguir con el resto.")
p.note("Pausar es inmediato, pero puede tardar un instante en vaciarse: si hay un bloque grande a medias, el motor espera a que termine de escribirse antes de salir de verdad parado. Es a propósito: cortar un bloque por la mitad dejaría el archivo incompleto sin avisar.")
p.h3("CLONAR LISTA Y LAS COLAS")
p.p("Cada dispositivo tiene su propia cola. Eso permite que uno vaya más adelante que otro sin que se estorben. Clonar lista lleva la cola de un dispositivo al resto sin mezclarlas.")
p.end()

# ================================================================== VENTANAS
p = P()
p.title("VENTANAS")
p.h3("LA VENTANA PRINCIPAL")
p.p("Es la única ventana del programa. No hay subventanas ni diálogos ocultos: todo está a la vista, en los cuatro bloques de la página APLICACIÓN.")
p.h3("EL BUSCADOR")
p.p("Pestaña del panel izquierdo. Campo de texto, botón para cambiar la carpeta de búsqueda y lista de resultados con tipo y tamaño. Ver el capítulo EL BUSCADOR.")
p.h3("COPIA EN CURSO")
p.p("Pestaña del panel izquierdo. Una tarjeta por dispositivo con trabajo: el archivo del origen en curso, dos barras de progreso, el estado, los archivos, la velocidad, el tiempo restante y el espacio libre. Ver el capítulo COPIA EN CURSO.")
p.h3("LA BARRA DE ESTADO")
p.p("Última fila de la ventana. A la izquierda, lo que está pasando en palabras. A la derecha, un resumen de la selección. Cuando algo falla, el mensaje aparece ahí.")
p.h3("LO QUE NO HAY")
p.b("No hay mini-explorer con vista de árbol, como en otros copiadores. La estructura se decide con el botón ESTRUCTURA, no explorando carpetas.")
p.b("No hay una lista de ficheros aparte con su propia ventana. Lo que se está copiando se ve en COPIA EN CURSO, integrada en la ventana.")
p.note("Es una decisión de diseño: menos ventanas sueltas significa que no hay que estar delante de la principal con las ventanas de por medio. Todo el trabajo se hace sin apartar la vista de la copia.")
p.end()

# ================================================================== CONFIG
p = P()
p.title("CONFIGURACIÓN")
p.p("En esta versión hay muy poco que configurar, y casi todo está a la vista.")
p.n("1", "Modo de copia, con el botón MODO. Se pone solo en Normal tras cada arrastre, aunque la copia empiece sola.")
p.n("2", "Estructura de carpetas, con el botón ESTRUCTURA.")
p.n("3", "Carpeta del buscador, con los tres puntos junto al campo de texto. Se recuerda al cerrar.")
p.n("4", "El intervalo de refresco de la ventana, con el botón de la barra de estado. Cada clic recorre una lista de valores entre 1 y 250 milisegundos, y el clic derecho vuelve atrás.")
p.note("Con un refresco de 1 a 3 milisegundos la copia en curso se ve muy fluida. Con 100 o 250 el equipo carga menos, y a los ojos casi no se nota la diferencia.")
p.h3("QUÉ SE GUARDA")
p.b("La carpeta de búsqueda del buscador.")
p.b("El intervalo de refresco elegido.")
p.b("Nada de lo que copias. Las colas de trabajo no se guardan entre sesiones: si cierras el programa a mitad de una copia, ese trabajo se pierde.")
p.note("Pendiente para versiones posteriores: guardar las colas y poder retomar un trabajo exactamente donde se dejó.")
p.end()

# ================================================================== FAQ
p = P()
p.title("PREGUNTAS FRECUENTES")
p.h3("¿POR QUÉ NO COPIA NADA?")
p.b("No hay ninguna tarjeta marcada, y has soltado fuera de las tarjetas. Entonces no hay a quién copiar, y el programa lo dice en la barra de estado.")
p.b("Lo que metiste en la cola con el buscador no está suelto sobre la ventana: hay que soltarlo sobre las tarjetas marcadas, o pulsar COPIAR.")
p.b("El destino no tiene espacio. Mira el espacio libre en la tarjeta.")
p.b("El destino está protegido contra escritura, o es de solo lectura.")
p.h3("EL ARCHIVO ACABA DENTRO DE UNA CARPETA CON LA LETRA")
p.b("Es la estructura de partida de los discos: se reproduce el árbol entero desde la raíz de la unidad, y por eso acaba en CarpetaConLaLetraDeLaUnidadUsuarios...")
p.b("Si prefieres la carpeta tal cual la soltaste, pon ESTRUCTURA en CARPETAS antes de arrastrar. El botón está en la barra de acciones.")
p.b("En las memorias USB no pasa: allí la estructura de partida ya es CARPETAS.")
p.h3("COPIA, PERO MUY DESPACIO")
p.b("El origen está fragmentado, o es un disco mecánico viejo.")
p.b("Muchos archivos muy pequeños: cada uno paga su coste de apertura.")
p.b("El destino está en FAT32 y hay que reescribir al cambiar el nombre.")
p.b("Hay otros programas leyendo o escribiendo la memoria al mismo tiempo.")
p.b("El antivirus de Windows revisa cada archivo recién escrito, y frena la copia.")
p.h3("CUANDO EL DESTINO DA PROBLEMAS")
p.b("Dice que no hay espacio, pero la memoria parece vacía: puede que no tenga formato, o que lo ocupe el archivo de intercambio de Windows, que no se ve.")
p.h3("SE HA IDO A LA MITAD Y EL ARCHIVO ESTÁ A MEDIAS")
p.b("Cierra el archivo del destino y vuelve a copiarlo. Un archivo a medias se detecta por tener un tamaño distinto del origen.")
p.b("Si el error se repite en el mismo archivo, prueba con otro programa, para descartar que el origen esté dañado.")
p.h3("¿Y LOS DISCOS INTERNOS Y EL DE ARRANQUE?")
p.p("Al disco interno sí puedes copiarle, pero no usarlo como origen mientras se escribe en él. El disco de arranque se ve en el listado pero no acepta arrastres: borrarlo dejaría el equipo sin arrancar.")
p.end()

# ================================================================== NO HACE
p = P()
p.title("LO QUE NO HACE")
p.p("Esta lista es exacta. Esta versión no hace nada de esto, y no lo simula:")
p.b("No formatea discos. No hay botón de formatear ni nada que lo sustituya.")
p.b("No cambia letras de unidad.")
p.b("No copia por red ni escribe en carpetas compartidas. Las ve en el listado, pero no trabaja con ellas.")
p.b("No trabaja con teléfonos ni con ningún otro dispositivo MTP.")
p.b("No indexa el contenido. No sabe qué son capítulos de una serie, ni cuáles son los capítulos del 1 al 10 de algo.")
p.b("No continúa series ni pregunta por la temporada siguiente. Ese automatismo necesita un índice previo, y aquí no lo hay.")
p.b("No crea archivos de texto con el nombre de un archivo, ni copia el nombre de un archivo al portapapeles.")
p.b("No guarda las colas entre sesiones. Si cierras a mitad, se pierde el trabajo de esa sesión.")
p.b("No hace capturas de pantalla de sus propias ventanas.")
p.b("No cambia de tema ni de color.")
p.b("No se queda en la bandeja del sistema. No hay icono de fondo.")
p.b("No hay ningún tipo de pago, suscripción, licencia ni registro. Es gratis, y no lo va a ser menos.")
p.note("De todas estas, las tres primeras tienen una solución sencilla: cambiar la letra de unidad y formatear se hacen desde el Explorador de Windows, y la copia por red se puede hacer con la propia carpeta compartida de Windows, en paralelo a este programa.")
p.end()

# ================================================================== REGISTRO
p = P()
p.title("EL REGISTRO")
p.p("Si algo va mal, el programa va dejando por dónde ha pasado. Se escribe en un único fichero de texto plano:")
p.b("%LOCALAPPDATA%\\FreemanMultiCopier\\registro.log", marker="arrow")
p.h3("QUÉ CONTIENE")
p.b("La hora exacta de cada arranque y de cada cierre.")
p.b("Los dispositivos que se han detectado y los que se han ido.")
p.b("El principio y el final de cada trabajo de copia, con su dispositivo y su número de archivos.")
p.b("Cada error, con su mensaje completo y su rastro.")
p.b("Los resultados de la prueba automática, si la has lanzado.")
p.h3("CÓMO USARLO")
p.n("1", "Abre el fichero con el Bloc de notas. Es texto normal, se puede leer directamente.")
p.n("2", "Busca la palabra ERROR para ir directo a los problemas.")
p.n("3", "Las últimas líneas suelen ser las más útiles: son las últimas cosas que pasaron.")
p.h3("LA PRUEBA AUTOMÁTICA")
p.p("El programa trae 82 comprobaciones que ejercitan el motor de verdad: crean archivos, los copian, comprueban el resultado, prueban los cuatro modos y las ocho estructuras, y borran todo al final. Para lanzarla:")
p.b('& "C:\\Program Files\\Freeman MULTICOPIER\\FreemanMultiCopier.exe" --selftest', marker="arrow")
p.p("El resultado se escribe en %LOCALAPPDATA%\\FreemanMultiCopier\\prueba.txt, una línea por comprobación, con el detalle de lo que falló si algo falló.")
p.end()

cv.save()
print("OK ->", OUT, os.path.getsize(OUT), "bytes,", cv.getPageNumber() - 1, "paginas")
