"""
自定义 UI 组件：圆角按钮 RoundedButton + 按钮工厂 create_button
"""

import tkinter as tk
import tkinter.font as tkfont


# ================= 圆角按钮组件 =================
class RoundedButton(tk.Canvas):
    """基于 Canvas 的圆角矩形按钮"""
    _instances = []
    font_family = 'Microsoft YaHei UI'  # 由 CloudBackupApp 在启动时覆盖

    @staticmethod
    def _darken(hex_color, factor=0.15):
        """将 hex 颜色变暗"""
        hex_color = hex_color.lstrip('#')
        r, g, b = int(hex_color[:2], 16), int(hex_color[2:4], 16), int(hex_color[4:6], 16)
        r = max(0, int(r * (1 - factor)))
        g = max(0, int(g * (1 - factor)))
        b = max(0, int(b * (1 - factor)))
        return f'#{r:02x}{g:02x}{b:02x}'

    def __init__(self, parent, text="", command=None, width=None, height=32,
                 bg="#E2E8F0", fg="#1E293B", hover_bg=None, radius=8,
                 font=None, padding=(14, 5), bold=False,
                 pack_side=None, pack_padx=None, pack_pady=None, pack_fill=None,
                 grid_row=None, grid_col=None, grid_padx=None, grid_pady=None,
                 grid_sticky=None, grid_columnspan=None, **kw):
        # 估算文本宽度以确定 width
        if width is None:
            test_font = tkfont.Font(family=font[0], size=font[1], weight='bold' if bold else 'normal')
            text_width = test_font.measure(text) + padding[0] * 2
            width = max(text_width + 6, 60)

        # 获取父容器背景色
        try:
            parent_bg = parent.cget('bg')
        except Exception:
            parent_bg = '#F5F6FA'

        super().__init__(parent, width=width, height=height,
                        bg=parent_bg, highlightthickness=0, bd=0, **kw)
        self._bg = bg
        self._fg = fg
        self._hover_bg = hover_bg if hover_bg else self._darken(bg)
        self._radius = radius
        self._command = command
        self._text = text
        if font is None:
            font = (self.__class__.font_family, 9)
        self._font = font
        self._bold = bold
        self._width = width
        self._height = height
        self._padding = padding
        self._hovering = False

        # 绑定事件
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<Button-1>", self._on_click)
        self.bind("<Configure>", self._on_resize)

        # 布局参数
        self._pack_side = pack_side
        self._pack_padx = pack_padx
        self._pack_pady = pack_pady
        self._pack_fill = pack_fill
        self._grid_row = grid_row
        self._grid_col = grid_col
        self._grid_padx = grid_padx
        self._grid_pady = grid_pady
        self._grid_sticky = grid_sticky
        self._grid_columnspan = grid_columnspan

        self._draw()
        self.__class__._instances.append(self)

    def _on_resize(self, event=None):
        self._draw()

    def _draw(self):
        self.delete("all")
        w = self.winfo_width()
        h = self.winfo_height()
        if w < 4 or h < 4:
            return
        r = self._radius
        color = self._hover_bg if self._hovering else self._bg

        # 绘制圆角矩形
        self.create_arc((0, 0, r*2, r*2), start=90, extent=90, fill=color, outline=color)
        self.create_arc((w-r*2-1, 0, w-1, r*2), start=0, extent=90, fill=color, outline=color)
        self.create_arc((0, h-r*2-1, r*2, h-1), start=180, extent=90, fill=color, outline=color)
        self.create_arc((w-r*2-1, h-r*2-1, w-1, h-1), start=270, extent=90, fill=color, outline=color)
        self.create_rectangle((r, 0, w-r-1, h-1), fill=color, outline=color)
        self.create_rectangle((0, r, w-1, h-r-1), fill=color, outline=color)

        # 绘制文本
        font_weight = 'bold' if self._bold else 'normal'
        self.create_text(w // 2, h // 2, text=self._text, fill=self._fg,
                        font=(self._font[0], self._font[1], font_weight))

    def _on_enter(self, event):
        self._hovering = True
        self._draw()

    def _on_leave(self, event):
        self._hovering = False
        self._draw()

    def _on_click(self, event):
        if self._command:
            self._command()

    def configure(self, **kw):
        if 'text' in kw:
            self._text = kw.pop('text')
        if 'command' in kw:
            self._command = kw.pop('command')
        if 'bg' in kw:
            self._bg = kw.pop('bg')
            self._hover_bg = self._darken(self._bg)
        if 'fg' in kw:
            self._fg = kw.pop('fg')
        if 'state' in kw:
            state = kw.pop('state')
            if state == 'disabled':
                self.unbind("<Enter>")
                self.unbind("<Leave>")
                self.unbind("<Button-1>")
            else:
                self.bind("<Enter>", self._on_enter)
                self.bind("<Leave>", self._on_leave)
                self.bind("<Button-1>", self._on_click)
        super().configure(**kw)
        self._draw()

    def cget(self, key):
        if key == 'text':
            return self._text
        return super().cget(key)

    def pack(self, **kw):
        # 保存参数以便后续可能的重新布局
        if 'side' in kw: self._pack_side = kw['side']
        if 'padx' in kw: self._pack_padx = kw['padx']
        if 'pady' in kw: self._pack_pady = kw['pady']
        if 'fill' in kw: self._pack_fill = kw['fill']
        super().pack(**kw)

    def grid(self, **kw):
        if 'row' in kw: self._grid_row = kw['row']
        if 'column' in kw: self._grid_col = kw['column']
        if 'padx' in kw: self._grid_padx = kw['padx']
        if 'pady' in kw: self._grid_pady = kw['pady']
        if 'sticky' in kw: self._grid_sticky = kw['sticky']
        if 'columnspan' in kw: self._grid_columnspan = kw['columnspan']
        super().grid(**kw)


# ================= 按钮工厂函数 =================
def create_button(parent, text, style, command, width=None, pack_side=None,
                  pack_padx=None, pack_pady=None, pack_fill=None,
                  grid_row=None, grid_col=None, grid_padx=None, grid_pady=None,
                  grid_sticky=None, grid_columnspan=None, **kw):
    """根据风格名称创建 RoundedButton，替换原有的 ttk.Button"""
    styles = {
        'Success.TButton': {'bg': '#10B981', 'fg': '#FFFFFF', 'bold': True, 'hover_bg': '#059669'},
        'Start.TButton': {'bg': '#10B981', 'fg': '#FFFFFF', 'bold': True, 'hover_bg': '#059669'},
        'Danger.TButton': {'bg': '#EF4444', 'fg': '#FFFFFF', 'bold': True, 'hover_bg': '#DC2626'},
        'Secondary.TButton': {'bg': '#E2E8F0', 'fg': '#1E293B', 'bold': False, 'hover_bg': '#CBD5E1'},
        'TButton': {'bg': '#E2E8F0', 'fg': '#1E293B', 'bold': False, 'hover_bg': '#CBD5E1'},
    }
    style_config = styles.get(style, styles['Secondary.TButton'])

    btn = RoundedButton(
        parent, text=text, command=command, width=width,
        bg=style_config['bg'], fg=style_config['fg'],
        hover_bg=style_config['hover_bg'], bold=style_config.get('bold', False),
        font=(RoundedButton.font_family, 9), padding=(14, 5), radius=8,
        pack_side=pack_side, pack_padx=pack_padx, pack_pady=pack_pady, pack_fill=pack_fill,
        grid_row=grid_row, grid_col=grid_col, grid_padx=grid_padx, grid_pady=grid_pady,
        grid_sticky=grid_sticky, grid_columnspan=grid_columnspan, **kw
    )
    if pack_side is not None or pack_padx is not None or pack_pady is not None or pack_fill is not None:
        pack_kw = {}
        if pack_side is not None: pack_kw['side'] = pack_side
        if pack_padx is not None: pack_kw['padx'] = pack_padx
        if pack_pady is not None: pack_kw['pady'] = pack_pady
        if pack_fill is not None: pack_kw['fill'] = pack_fill
        btn.pack(**pack_kw)
    elif grid_row is not None or grid_col is not None:
        grid_kw = {}
        if grid_row is not None: grid_kw['row'] = grid_row
        if grid_col is not None: grid_kw['column'] = grid_col
        if grid_padx is not None: grid_kw['padx'] = grid_padx
        if grid_pady is not None: grid_kw['pady'] = grid_pady
        if grid_sticky is not None: grid_kw['sticky'] = grid_sticky
        if grid_columnspan is not None: grid_kw['columnspan'] = grid_columnspan
        btn.grid(**grid_kw)
    return btn
