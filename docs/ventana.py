import fitz

# Dibuja docs\\ventana.png: un boceto de la ventana con datos de ejemplo, para
# el README del repositorio. No es una captura: es un dibujo, hecho con la
# misma paleta y la misma disposicion que src\\FreemanMultiCopier\\UI.
#
# Motivo de que sea un dibujo y no una captura: con las unidades del cliente
# conectadas, una captura real enseñaria sus nombres de volumen. Aqui las
# unidades son inventadas.
#
#   python docs\\ventana.py
#
# Necesita pymupdf.

# Paleta sacada de src/FreemanMultiCopier/UI/Themes/Dark.xaml
SURFACE   = (0x1E / 255, 0x1F / 255, 0x24 / 255)
SURFACE_A = (0x26 / 255, 0x28 / 255, 0x2F / 255)
CARD      = (0x2B / 255, 0x2E / 255, 0x36 / 255)
BORDER    = (0x3A / 255, 0x3E / 255, 0x48 / 255)
TEXT      = (0xF2 / 255, 0xF3 / 255, 0xF5 / 255)
TEXT_DIM  = (0x9A / 255, 0xA0 / 255, 0xAC / 255)
ACCENT    = (0x3D / 255, 0x8B / 255, 0xFF / 255)
OK        = (0x3F / 255, 0xB9 / 255, 0x50 / 255)
WARN      = (0xD2 / 255, 0x99 / 255, 0x22 / 255)
ERR       = (0xE5 / 255, 0x53 / 255, 0x4B / 255)
BLANCO    = (1, 1, 1)

W, H = 1180, 720
doc = fitz.open()
page = doc.new_page(width=W, height=H)
sh = page.new_shape()


def rect(x, y, w, h, fill=None):
    sh.draw_rect(fitz.Rect(x, y, x + w, y + h))
    sh.finish(fill=fill)


def rrect(x, y, w, h, fill=None, stroke=None, rad=6, lw=1.0):
    # PyMuDF pide el radio como fraccion del lado menor, no en puntos
    fr = min(0.5, rad / max(1.0, min(w, h)))
    sh.draw_rect(fitz.Rect(x, y, x + w, y + h), radius=fr)
    sh.finish(fill=fill, color=stroke, width=lw)


def line(x0, y0, x1, y1, color, lw=1.0):
    sh.draw_line(fitz.Point(x0, y0), fitz.Point(x1, y1))
    sh.finish(color=color, width=lw)


def txt(x, y, s, size=11, color=TEXT, font="helv", align=0):
    if align == 1:
        x -= fitz.get_text_length(s, font, size)
    elif align == 2:
        x -= fitz.get_text_length(s, font, size) / 2
    page.insert_text((x, y), s, fontname=font, fontsize=size, color=color)


def progress(x, y, w, h, frac, color):
    rrect(x, y, w, h, fill=(0x1A / 255, 0x1B / 255, 0x1F / 255), rad=h / 2)
    if frac > 0:
        rrect(x, y, max(w * frac, h), h, fill=color, rad=h / 2)


# ------------------------------------------------------------------- fondo
rect(0, 0, W, H, fill=SURFACE)

# --------------------------------------------------------- barra superior
rect(0, 0, W, 40, fill=SURFACE_A)
line(0, 40, W, 40, BORDER)
txt(14, 25, "Freeman MULTICOPIER", 15, TEXT)
txt(178, 25, "v0.1", 12, TEXT_DIM)
x = W - 14
for s in ["Dispositivos: 6", "Seleccionados: 5", "Trabajando: 5", "RAM: 48 MB"]:
    txt(x, 25, s, 11, TEXT_DIM, align=1)
    x -= fitz.get_text_length(s, "helv", 11) + 16
txt(x, 25, "742 MB/s", 12, ACCENT, font="cour", align=1)

# ------------------------------------------------------ panel izquierdo 2*
LX, LW = 12, 452
y = 52

txt(LX, y, "Buscador", 13, TEXT)
y += 12
rrect(LX, y, LW - 40, 26, fill=CARD, stroke=BORDER, rad=5)
txt(LX + 9, y + 17, "temporada 1", 11, TEXT)
rrect(LX + LW - 34, y, 34, 26, fill=SURFACE_A, stroke=BORDER, rad=5)
txt(LX + LW - 17, y + 17, "...", 11, TEXT_DIM, align=2)
y += 34
txt(LX, y, "D:\\Series", 10.5, TEXT_DIM)
y += 15
txt(LX, y, "24 resultados", 10.5, TEXT_DIM)
y += 14

for nombre, detalle in [("peli-01.mkv", "video  \u00b7  4,21 GB"),
                        ("peli-02.mkv", "video  \u00b7  3,88 GB"),
                        ("peli-03.mkv", "video  \u00b7  5,02 GB")]:
    rrect(LX, y, LW, 32, fill=CARD, rad=5)
    txt(LX + 9, y + 14, nombre, 11, TEXT)
    txt(LX + 9, y + 26, detalle, 9.5, TEXT_DIM)
    y += 36

# ---------------------------------------------------------- bloque ORIGEN
y += 10
txt(LX, y, "Copia en curso", 13, TEXT)
y += 15
txt(LX, y, "Lo que se lee del origen y cuanto queda libre en cada destino.", 10, TEXT_DIM)
y += 16

rrect(LX, y, LW, 108, fill=CARD, stroke=BORDER, rad=8)
txt(LX + 14, y + 22, "ORIGEN", 11, ACCENT)
txt(LX + LW - 14, y + 22, "1,84 GB", 11, TEXT_DIM, align=1)
txt(LX + 14, y + 45, "peli-01.mkv", 14, TEXT)
txt(LX + 14, y + 61, "D:\\Series\\Temporada 1", 10, TEXT_DIM)
line(LX + 14, y + 70, LX + LW - 14, y + 70, BORDER)
txt(LX + 14, y + 86, "LECTURA   532,8 MB/s", 11, OK, font="cour")
txt(LX + LW - 14, y + 86, "ADEMAS   1,3 GB/s hacia 4 destinos", 10.5, TEXT_DIM, align=1)
txt(LX + 14, y + 100, "5 en cola, 2 mas a la vez", 10, TEXT_DIM)
y += 120

txt(LX, y, "Dispositivos", 13, TEXT)
y += 15
txt(LX, y, "Estructura: arbol del origen", 10.5, TEXT_DIM)
y += 16
rrect(LX, y, LW, 24, fill=SURFACE_A, rad=5)
txt(LX + 9, y + 16, "Modo: Normal", 10.5, TEXT)

# ------------------------------------------------------- panel derecho 3*
line(478, 46, 478, H - 52, BORDER)
RX, RW = 492, W - 492 - 12

tarjetas = [
    ("PENDRIVE 128 GB (E:)",  "memoria USB 3.2",    0.41, "74 GB",  "186 MB/s", "Copiando",    OK),
    ("DISCO EXTERNO 2 TB (F:)", "disco duro portatil", 0.41, "1,2 TB", "94 MB/s",  "Copiando",    OK),
    ("SERVIDOR NAS (N:)",       "carpeta de red",     0.41, "3,4 TB", "212 MB/s", "Copiando",    OK),
    ("UNIDAD EXTERNA (P:)",     "disco duro portatil", 0.41, "640 GB", "38 MB/s",  "Copiando",    WARN),
    ("MEMORIA VIEJA (M:)",      "memoria USB 2.0",    0.00, "0 B",    "0 MB/s",   "Sin espacio", ERR),
]
y = 52
for nombre, tipo, frac, libre, vel, estado, col in tarjetas:
    alto = 84
    rrect(RX, y, RW, alto, fill=CARD, stroke=ERR if col is ERR else BORDER, rad=8)
    rect(RX + 1, y + 10, 3, alto - 20, fill=col)

    txt(RX + 16, y + 24, nombre, 12.5, TEXT)
    txt(RX + 16, y + 41, tipo, 10, TEXT_DIM)
    txt(RX + RW - 16, y + 24, "%.0f%%" % (frac * 100), 12.5, col, align=1)
    txt(RX + RW - 16, y + 41, vel, 10.5, TEXT_DIM, font="cour", align=1)

    progress(RX + 16, y + 50, RW - 32, 7, frac, col)
    txt(RX + 16, y + 74, "Libre " + libre, 10, TEXT_DIM)
    txt(RX + 150, y + 74, "archivos: 412", 10, TEXT_DIM)
    txt(RX + RW - 16, y + 74, estado, 10, col, align=1)
    y += alto + 8

# --------------------------------------------------------- barra inferior
rect(0, H - 52, W, 52, fill=SURFACE_A)
line(0, H - 52, W, H - 52, BORDER)
txt(14, H - 24, "Modo: Normal", 11, TEXT)
txt(110, H - 24, "Estructura: arbol del origen", 11, TEXT_DIM)
txt(320, H - 24, "Soltar es copiar", 11, TEXT_DIM)

rrect(W - 470, H - 40, 96, 30, fill=ACCENT, rad=6)
txt(W - 422, H - 20, "COPIAR", 12, BLANCO, align=2)
for i, boton in enumerate(["Pausar todo", "Continuar todo", "Seleccionar todo",
                           "Reintentar", "Expulsar"]):
    bx = W - 364 + i * 70
    rrect(bx, H - 40, 66, 30, fill=CARD, stroke=BORDER, rad=6)
    txt(bx + 33, H - 20, boton, 9, TEXT_DIM, align=2)

sh.commit()
pix = page.get_pixmap(matrix=fitz.Matrix(2, 2))
salida = r"C:\Users\FREEMAN\Documents\Default Project\FreemanMultiCopier\docs\ventana.png"
pix.save(salida)
print("dibujada %s  %d x %d px" % (salida, pix.width, pix.height))
