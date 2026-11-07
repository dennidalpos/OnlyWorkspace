"""Generate social cards: 01-04 use installed captures dated 2026-10-21; 05-07 are illustrations.

Produces a unified, single-format set of PNG visual cards directly in assets/social/:
- 01-copertina.png
- 02-sintesi-email.png
- 03-verifica-sicurezza.png
- 04-estrai-dati.png
- 05-bozze-scrittura.png
- 06-ricostruisci-flusso.png
- 07-privacy-sovranita.png

All files are 1080x1350 px (Portrait 4:5, optimal for LinkedIn, Instagram, and Facebook).
"""

from io import BytesIO
import os
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
SOCIAL = ASSETS / "social"
SCREENSHOTS = ASSETS / "screenshots" / "installed"

NAVY_DARK = "#08101E"
NAVY_LIGHT = "#10233B"
PANEL_BG = "#0E1A2C"
PANEL_BORDER = "#223B5A"
TEXT_WHITE = "#F4F8FF"
TEXT_MUTED = "#9DB3CD"
TEXT_DIM = "#6A84A4"
ACCENT_BLUE = "#65B2FF"
ACCENT_MINT = "#62E6C6"
ACCENT_YELLOW = "#FFC83D"
MARK_BLUE = "#1A5FB4"

WIN_DIR = os.environ.get("WINDIR", "C:\\Windows")
FONT_DIR = os.path.join(WIN_DIR, "Fonts")

def get_font(name: str, size: int) -> ImageFont.FreeTypeFont:
    path = os.path.join(FONT_DIR, name)
    if os.path.exists(path):
        return ImageFont.truetype(path, size)
    fallback = os.path.join(FONT_DIR, "arial.ttf")
    return ImageFont.truetype(fallback, size)

F_TITLE = lambda sz: get_font("segoeuib.ttf", sz)
F_SEMIBOLD = lambda sz: get_font("segoeuisl.ttf" if sz < 18 else "segoeui.ttf", sz)
F_REGULAR = lambda sz: get_font("segoeui.ttf", sz)

def load_icon(size: int = 48) -> Image.Image:
    icon_path = ASSETS / "brand" / "icon.png"
    if icon_path.exists():
        im = Image.open(icon_path).convert("RGBA")
        return im.resize((size, size), Image.Resampling.LANCZOS)
    im = Image.new("RGBA", (size, size), (26, 95, 180, 255))
    return im

def create_background(w: int, h: int) -> Image.Image:
    """Create a dark theme background with a soft ambient radial glow."""
    base = Image.new("RGBA", (w, h), (8, 16, 30, 255))
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(glow)
    
    cx, cy = int(w * 0.85), int(h * 0.18)
    radius = int(max(w, h) * 0.55)
    for r in range(radius, 0, -25):
        alpha = int(45 * (1.0 - (r / radius) ** 1.8))
        if alpha > 0:
            gdraw.ellipse(
                [cx - r, cy - r, cx + r, cy + r],
                fill=(24, 85, 145, alpha)
            )
            
    cx2, cy2 = int(w * 0.12), int(h * 0.85)
    radius2 = int(max(w, h) * 0.4)
    for r in range(radius2, 0, -30):
        alpha = int(22 * (1.0 - (r / radius2) ** 1.8))
        if alpha > 0:
            gdraw.ellipse(
                [cx2 - r, cy2 - r, cx2 + r, cy2 + r],
                fill=(18, 55, 95, alpha)
            )
            
    base.alpha_composite(glow)
    return base

def draw_header(draw: ImageDraw.ImageDraw, x: int, y: int, width: int, icon_im: Image.Image, category_text: str, base_im: Image.Image):
    base_im.alpha_composite(icon_im, (x, y))
    f_brand = F_TITLE(28)
    draw.text((x + icon_im.width + 14, y + int((icon_im.height - 28) / 2) - 2), "OnlyWorkspace", font=f_brand, fill=TEXT_WHITE)
    
    f_cat = F_TITLE(16)
    bbox = draw.textbbox((0, 0), category_text, font=f_cat)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    pill_w = tw + 28
    pill_h = 36
    pill_x = x + width - pill_w
    pill_y = y + int((icon_im.height - pill_h) / 2)
    
    draw.rounded_rectangle([pill_x, pill_y, pill_x + pill_w, pill_y + pill_h], radius=18, fill="#12273F", outline="#224266", width=1)
    draw.text((pill_x + 14, pill_y + int((pill_h - th) / 2) - 3), category_text, font=f_cat, fill=ACCENT_BLUE)

def draw_footer(draw: ImageDraw.ImageDraw, x: int, y: int, width: int, page_str: str):
    draw.line([(x, y), (x + width, y)], fill="#1B324D", width=1)
    f_sub = F_REGULAR(17)
    draw.text((x, y + 16), "Microsoft Outlook classico (2016+)  ·  Windows x86 e x64", font=f_sub, fill=TEXT_DIM)
    if page_str:
        f_page = F_TITLE(20)
        pbbox = draw.textbbox((0, 0), page_str, font=f_page)
        pw = pbbox[2] - pbbox[0]
        draw.text((x + width - pw, y + 14), page_str, font=f_page, fill=ACCENT_BLUE)

def draw_mockup_window(
    base_im: Image.Image,
    x: int, y: int, w: int, h: int,
    content_im: Image.Image,
    title: str = "Microsoft Outlook · OnlyWorkspace",
    draw: ImageDraw.ImageDraw = None
):
    if draw is None:
        draw = ImageDraw.Draw(base_im)
    
    draw.rounded_rectangle([x, y, x + w, y + h], radius=18, fill=PANEL_BG, outline=PANEL_BORDER, width=2)
    
    bar_h = 38
    draw.rounded_rectangle([x, y, x + w, y + bar_h + 12], radius=18, fill="#15263C")
    draw.rectangle([x, y + bar_h, x + w, y + bar_h + 12], fill="#15263C")
    draw.line([(x, y + bar_h + 12), (x + w, y + bar_h + 12)], fill=PANEL_BORDER, width=1)
    
    for i, col in enumerate(["#E8554E", "#FFB636", "#2AC747"]):
        draw.ellipse([x + 18 + i * 18, y + 15, x + 28 + i * 18, y + 25], fill=col)
        
    f_bar = F_REGULAR(15)
    tbbox = draw.textbbox((0, 0), title, font=f_bar)
    tw = tbbox[2] - tbbox[0]
    draw.text((x + int((w - tw) / 2), y + 10), title, font=f_bar, fill=TEXT_MUTED)
    
    inner_x = x + 2
    inner_y = y + bar_h + 13
    inner_w = w - 4
    inner_h = h - bar_h - 15
    
    cw, ch = content_im.size
    scale = min(inner_w / cw, inner_h / ch)
    target_w = int(cw * scale)
    target_h = int(ch * scale)
    
    resized = content_im.resize((target_w, target_h), Image.Resampling.LANCZOS)
    px = inner_x + int((inner_w - target_w) / 2)
    py = inner_y + int((inner_h - target_h) / 2)
    
    if resized.mode != "RGBA":
        resized = resized.convert("RGBA")
    base_im.alpha_composite(resized, (px, py))

def draw_pill(
    draw: ImageDraw.ImageDraw,
    x: int, y: int,
    text: str,
    font: ImageFont.FreeTypeFont,
    bg: str = "#122A44",
    border: str = "#65B2FF",
    fg: str = "#FFFFFF",
    pad_h: int = 12,
    pad_v: int = 7
) -> int:
    bbox = draw.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    pw = tw + pad_h * 2
    ph = th + pad_v * 2 + 2
    draw.rounded_rectangle([x, y, x + pw, y + ph], radius=ph // 2, fill=bg, outline=border, width=1)
    draw.text((x + pad_h, y + pad_v - 1), text, font=font, fill=fg)
    return pw

def safe_save_png(im: Image.Image, dest_path: Path):
    """Save PNG using in-memory buffer and atomic replace to prevent Windows file locking issues."""
    buf = BytesIO()
    im.save(buf, format="PNG", optimize=True)
    data = buf.getvalue()
    
    dest_path.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(5):
        temp_path = dest_path.with_name(f"{dest_path.name}.tmp.{attempt}")
        try:
            temp_path.write_bytes(data)
            temp_path.replace(dest_path)
            return
        except OSError:
            import time
            time.sleep(0.15)
        finally:
            if temp_path.exists():
                try:
                    temp_path.unlink()
                except OSError:
                    pass
    dest_path.write_bytes(data)

SLIDES_DATA = [
    (
        "01-copertina.png",
        "IA PER OUTLOOK",
        "L'ASSISTENTE INTELLIGENTE INTEGRATO",
        "La tua posta.\nUn contesto chiaro.",
        "Basta perdere ore a cercare tra centinaia di email o a decifrare allegati: OnlyWorkspace porta l'assistente IA direttamente dentro Microsoft Outlook classico, per lavorare più veloce e senza stress.",
        "hero"
    ),
    (
        "02-sintesi-email.png",
        "SINTESI EMAIL",
        "DALLA CONFUSIONE AI PUNTI CHIAVE",
        "Capisci al volo le\nemail complesse.",
        "Ricevi messaggi chilometrici con decine di risposte e allegati? In un clic l'app estrae le decisioni già concordate, i punti ancora da risolvere e chi deve rispondere.",
        "sintesi"
    ),
    (
        "03-verifica-sicurezza.png",
        "VERIFICA SICUREZZA",
        "PROTEZIONE ANTI-FRODE E PHISHING",
        "Riconosci le trappole\nprima di cliccare.",
        "Un mittente ti chiede pagamenti urgenti o credenziali? OnlyWorkspace controlla intestazioni, link e allegati sospetti, mostrando indizi tecnici e rischi da valutare prima di agire.",
        "sicurezza"
    ),
    (
        "04-estrai-dati.png",
        "DATI & TABELLE",
        "DA DOCUMENTI A EXCEL IN UN CLIC",
        "Preventivi e ordini\nsubito in tabella.",
        "Basta trascrivere numeri a mano. L'app isola articoli, codici, quantità e importi da email o preventivi e li organizza in una tabella pronta da copiare o salvare in CSV.",
        "dati"
    ),
    (
        "05-bozze-scrittura.png",
        "SCRITTURA ASSISTITA",
        "BOZZE RAPIDE CON CONTROLLO TOTALE",
        "Rispondi in un attimo,\nma decidi sempre tu.",
        "Chiedi una bozza formale, cordiale o sintetica. Il testo compare nel riquadro: lo leggi, lo perfezioni e decidi tu quando inviarlo. Nessun messaggio parte mai in automatico.",
        "scrittura"
    ),
    (
        "06-ricostruisci-flusso.png",
        "CRONOLOGIA & FLUSSO",
        "RICOSTRUZIONE DELLA CORRISPONDENZA",
        "Ritrova il filo,\nanche dopo mesi.",
        "Dov'era finita quella decisione presa a maggio? Cerca tra tutte le tue cartelle e ricostruisci la cronologia dei messaggi collegati tra loro, con riferimenti chiari e verificabili.",
        "flusso"
    ),
    (
        "07-privacy-sovranita.png",
        "PRIVACY & SOVRANITÀ",
        "SICUREZZA E PROTEZIONE DATI",
        "Archivio sul PC.\nDecidi dove usare l'IA.",
        "L'archivio è cifrato sul PC. Il modello può essere locale o cloud. La ricerca web usa servizi esterni anche con Ollama locale: gli invii richiedono l'informativa accettata per le destinazioni.",
        "privacy"
    ),
]

def render_slide(slide_index: int, icon_im: Image.Image) -> Image.Image:
    w, h = 1080, 1350
    base = create_background(w, h)
    draw = ImageDraw.Draw(base)
    
    margin_x = int(w * 0.055)
    content_w = w - margin_x * 2
    
    header_y = int(h * 0.045)
    footer_y = h - int(h * 0.065)
    
    fname, cat, eyebrow, headline, practical_desc, kind = SLIDES_DATA[slide_index - 1]
    
    draw_header(draw, margin_x, header_y, content_w, icon_im, cat, base)
    
    eyebrow_y = header_y + icon_im.height + 22
    f_eye = F_TITLE(17)
    draw.text((margin_x, eyebrow_y), eyebrow, font=f_eye, fill=ACCENT_MINT)
    
    head_y = eyebrow_y + 30
    f_head = F_TITLE(48)
    draw.text((margin_x, head_y), headline, font=f_head, fill=TEXT_WHITE, spacing=8)
    
    head_lines = len(headline.split("\n"))
    desc_y = head_y + (head_lines * 54) + 16
    f_desc = F_REGULAR(23)
    
    words = practical_desc.split(" ")
    lines = []
    cur = []
    max_desc_w = content_w - 40
    for w_word in words:
        test_line = " ".join(cur + [w_word])
        bb = draw.textbbox((0, 0), test_line, font=f_desc)
        if (bb[2] - bb[0]) > max_desc_w:
            lines.append(" ".join(cur))
            cur = [w_word]
        else:
            cur.append(w_word)
    if cur:
        lines.append(" ".join(cur))
        
    draw.text((margin_x, desc_y), "\n".join(lines), font=f_desc, fill=TEXT_MUTED, spacing=8)
    
    mock_top = desc_y + (len(lines) * 33) + 24
    mock_bottom = footer_y - 52
    mock_h = mock_bottom - mock_top
    
    if kind == "hero":
        im_source = Image.open(SCREENSHOTS / "01-sintesi.png")
        draw_mockup_window(base, margin_x, mock_top, content_w, mock_h, im_source, "Microsoft Outlook con OnlyWorkspace", draw)
        
        badge_y = mock_bottom - 68
        bx = margin_x + 18
        bx += draw_pill(draw, bx, badge_y, "Sintesi & Bozze", F_TITLE(16), bg="#0B2642", border="#65B2FF", fg="#FFFFFF") + 12
        bx += draw_pill(draw, bx, badge_y, "Verifica Anti-Frode", F_TITLE(16), bg="#0B2642", border="#62E6C6", fg="#FFFFFF") + 12
        draw_pill(draw, bx, badge_y, "Archivio cifrato sul PC", F_TITLE(16), bg="#0B2642", border="#FFC83D", fg="#FFFFFF")
        
    elif kind == "sintesi":
        raw = Image.open(SCREENSHOTS / "01-sintesi.png")
        cropped = raw.crop((580, 120, 1920, 990))
        draw_mockup_window(base, margin_x, mock_top, content_w, mock_h, cropped, "Sintesi dell'email selezionata · Progetto Aurora", draw)
        
        badge_y = mock_bottom - 64
        draw_pill(draw, margin_x + 22, badge_y, "Punti chiave, scadenze e decisioni estratti in un clic", F_TITLE(16), bg="#0A2D3D", border="#62E6C6", fg="#E2FDF5")
        
    elif kind == "sicurezza":
        raw = Image.open(SCREENSHOTS / "02-verifica-sicurezza.png")
        cropped = raw.crop((580, 120, 1920, 990))
        draw_mockup_window(base, margin_x, mock_top, content_w, mock_h, cropped, "Verifica sicurezza e anti-phishing · Giudizio tecnico", draw)
        
        badge_y = mock_bottom - 64
        draw_pill(draw, margin_x + 22, badge_y, "Allerta immediata: link ingannevoli e domini sospetti segnalati", F_TITLE(16), bg="#3B1C1D", border="#FF7B72", fg="#FFDFDD")
        
    elif kind == "dati":
        raw = Image.open(SCREENSHOTS / "03-estrai-dati.png")
        cropped = raw.crop((580, 120, 1920, 990))
        draw_mockup_window(base, margin_x, mock_top, content_w, mock_h, cropped, "Estrazione tabella dati · Ordine AUR-2026-118", draw)
        
        badge_y = mock_bottom - 64
        draw_pill(draw, margin_x + 22, badge_y, "Esporta in CSV o incolla in Excel con un clic", F_TITLE(16), bg="#0A2C3D", border="#65B2FF", fg="#E1F0FF")
        
    elif kind == "scrittura":
        card = Image.new("RGBA", (content_w, mock_h), (14, 26, 44, 255))
        cdraw = ImageDraw.Draw(card)
        cdraw.rounded_rectangle([0, 0, content_w, mock_h], radius=18, fill=PANEL_BG, outline=PANEL_BORDER, width=2)
        
        cdraw.rounded_rectangle([20, 24, content_w - 20, 74], radius=12, fill="#15273E", outline="#254266", width=1)
        cdraw.text((36, 38), "Oggetto:", font=F_TITLE(18), fill=TEXT_MUTED)
        cdraw.text((120, 38), "Re: Offerta per rinnovo licenze e supporto annuale", font=F_REGULAR(18), fill=TEXT_WHITE)
        
        bx = 20
        for style_name, is_sel in [("Più formale", True), ("Più cordiale", False), ("Più breve", False), ("Traduci", False)]:
            bw = 145
            bg_pill = "#1A4168" if is_sel else "#132338"
            b_border = ACCENT_BLUE if is_sel else "#223B58"
            t_col = TEXT_WHITE if is_sel else TEXT_MUTED
            cdraw.rounded_rectangle([bx, 94, bx + bw, 134], radius=20, fill=bg_pill, outline=b_border, width=1)
            cdraw.text((bx + 20, 104), style_name, font=F_TITLE(15), fill=t_col)
            bx += bw + 14
            
        draft_y = 154
        draft_h = mock_h - 240
        cdraw.rounded_rectangle([20, draft_y, content_w - 20, draft_y + draft_h], radius=14, fill="#0A1424", outline="#1F3652", width=1)
        
        draft_lines = [
            "Gentile Dott.ssa Rossi,",
            "",
            "La ringrazio per la proposta inoltrata. Confermo che abbiamo esaminato",
            "il capitolato tecnico aggiornato e concordiamo con i termini di consegna previsti.",
            "Le confermo la presa in carico per procedere con la formalizzazione dell'ordine.",
            "",
            "Resto a disposizione per qualsiasi ulteriore chiarimento.",
            "Cordiali saluti,"
        ]
        cdraw.text((40, draft_y + 24), "\n".join(draft_lines), font=F_REGULAR(18), fill="#E6EEF8", spacing=7)
        
        btn_y = draft_y + draft_h + 18
        cdraw.rounded_rectangle([20, btn_y, 250, btn_y + 46], radius=23, fill=MARK_BLUE)
        cdraw.text((45, btn_y + 12), "Inserisci al cursore", font=F_TITLE(16), fill=TEXT_WHITE)
        
        cdraw.rounded_rectangle([265, btn_y, 515, btn_y + 46], radius=23, fill="#17314C", outline="#294A70", width=1)
        cdraw.text((285, btn_y + 12), "Sostituisci selezione", font=F_TITLE(16), fill=ACCENT_BLUE)
        
        cdraw.text((540, btn_y + 14), "Nessun invio automatico: decidi tu", font=F_TITLE(15), fill=ACCENT_MINT)
        
        base.alpha_composite(card, (margin_x, mock_top))
        
    elif kind == "flusso":
        card = Image.new("RGBA", (content_w, mock_h), (14, 26, 44, 255))
        cdraw = ImageDraw.Draw(card)
        cdraw.rounded_rectangle([0, 0, content_w, mock_h], radius=18, fill=PANEL_BG, outline=PANEL_BORDER, width=2)
        
        cdraw.line([(70, 70), (70, mock_h - 70)], fill=ACCENT_BLUE, width=3)
        
        steps = [
            ("M1", "15 Settembre · Richiesta iniziale", "Email ricevuta con capitolato e richiesta offerta", "#65B2FF"),
            ("M2", "22 Settembre · Proposta economica", "Preventivo condiviso con dettaglio articoli e sconto", "#62E6C6"),
            ("M3", "04 Ottobre · Accettazione e consegna", "Conferma finale con termine consegna per venerdì", "#FFC83D"),
        ]
        
        step_gap = (mock_h - 140) // 3
        for i, (tag, stitle, sdesc, col) in enumerate(steps):
            sy = 50 + i * step_gap
            cdraw.ellipse([58, sy + 20, 82, sy + 44], fill=col)
            
            box_x = 110
            box_w = content_w - box_x - 30
            box_h = step_gap - 18
            cdraw.rounded_rectangle([box_x, sy, box_x + box_w, sy + box_h], radius=14, fill="#132338", outline="#203956", width=1)
            
            cdraw.rounded_rectangle([box_x + 18, sy + 18, box_x + 72, sy + 46], radius=14, fill="#1B3B5E")
            cdraw.text((box_x + 30, sy + 23), tag, font=F_TITLE(15), fill=TEXT_WHITE)
            
            cdraw.text((box_x + 85, sy + 23), stitle, font=F_TITLE(18), fill=TEXT_WHITE)
            cdraw.text((box_x + 20, sy + 58), sdesc, font=F_REGULAR(16), fill=TEXT_MUTED)
            
        base.alpha_composite(card, (margin_x, mock_top))
        
    elif kind == "privacy":
        card = Image.new("RGBA", (content_w, mock_h), (14, 26, 44, 255))
        cdraw = ImageDraw.Draw(card)
        cdraw.rounded_rectangle([0, 0, content_w, mock_h], radius=18, fill=PANEL_BG, outline=PANEL_BORDER, width=2)
        
        col_w = (content_w - 60) // 2
        col_h = mock_h - 140
        
        cdraw.rounded_rectangle([20, 20, 20 + col_w, 20 + col_h], radius=16, fill="#0E2135", outline="#1F4166", width=1)
        cdraw.text((40, 40), "Ollama Locale", font=F_TITLE(22), fill=ACCENT_MINT)
        cdraw.text((40, 72), "Inferenza sul PC, senza web", font=F_TITLE(16), fill=TEXT_MUTED)
        
        for idx, text_item in enumerate(["Modello su endpoint loopback", "Senza servizio IA cloud obbligatorio", "Web Search: servizio esterno", "Senza web: analisi anche offline"]):
            cdraw.ellipse([40, 126 + idx * 46, 50, 136 + idx * 46], fill=ACCENT_MINT)
            cdraw.text((62, 121 + idx * 46), text_item, font=F_REGULAR(17), fill=TEXT_WHITE)
            
        c2_x = 20 + col_w + 20
        cdraw.rounded_rectangle([c2_x, 20, c2_x + col_w, 20 + col_h], radius=16, fill="#0E2135", outline="#1F4166", width=1)
        cdraw.text((c2_x + 20, 40), "Provider Cloud", font=F_TITLE(22), fill=ACCENT_BLUE)
        cdraw.text((c2_x + 20, 72), "Servizio e modello configurati", font=F_TITLE(16), fill=TEXT_MUTED)
        
        for idx, text_item in enumerate(["Testi inviati al provider scelto", "Informativa per le destinazioni", "Livello di invio per profilo", "Uso dati: contratto e piano effettivo"]):
            cdraw.ellipse([c2_x + 20, 126 + idx * 46, c2_x + 30, 136 + idx * 46], fill=ACCENT_BLUE)
            cdraw.text((c2_x + 42, 121 + idx * 46), text_item, font=F_REGULAR(17), fill=TEXT_WHITE)
            
        bot_y = 20 + col_h + 18
        cdraw.rounded_rectangle([20, bot_y, content_w - 20, mock_h - 20], radius=14, fill="#142B44", outline="#254B75", width=1)
        cdraw.text((40, bot_y + 16), "Archivio SQLite cifrato · Chiave protetta da DPAPI Windows", font=F_TITLE(17), fill=TEXT_WHITE)
        
        base.alpha_composite(card, (margin_x, mock_top))

    provenance = "Da catture installate · 21/10/2026 · Dati sintetici" if slide_index <= 4 else "Illustrazione delle funzioni · Non è una schermata dell'app"
    draw.text((margin_x, mock_bottom + 16), provenance, font=F_REGULAR(17), fill=TEXT_MUTED)
    page_str = f"0{slide_index} / 07"
    draw_footer(draw, margin_x, footer_y, content_w, page_str)
    
    return base.convert("RGB")

def main():
    icon_im = load_icon(52)
    SOCIAL.mkdir(parents=True, exist_ok=True)
    
    print("Generating OnlyWorkspace Social Media Assets (Single PNG format, 1080x1350)...")
    for i, item in enumerate(SLIDES_DATA, 1):
        fname = item[0]
        im = render_slide(i, icon_im)
        out_path = SOCIAL / fname
        safe_save_png(im, out_path)
        print(f"  -> {fname} ({im.size})")

    print("\nAll 7 social assets successfully generated in assets/social/!")

if __name__ == "__main__":
    main()
