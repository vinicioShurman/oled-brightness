namespace Brilho;

/// Escurecimento por uma camada preta semitransparente por cima de tudo, que
/// deixa os cliques passarem. É o espelho, no sentido contrário, da
/// sobreposição "multiply" do Extra Brightness do Vorssaint: preto com alfa a
/// multiplica cada pixel por (1 - a). Serve quando a curva de gama não vale
/// (HDR ligado) e morre junto com o app, então nada fica escuro depois de uma
/// falha. Não escurece o cursor do mouse.
sealed class OverlaySet : IDisposable
{
    // Duas metades em vez de uma janela que cobre a tela inteira: uma janela
    // "tela cheia" no topo faz o Windows achar que um jogo está aberto e
    // segurar notificações (o Vorssaint divide pelo mesmo tipo de motivo no Mac).
    readonly OverlayWindow upper = new(), lower = new();

    public bool Visible => upper.Visible;

    /// `linearLight`: com HDR o compositor mistura em luz linear, então o alfa
    /// é corrigido para escurecer o mesmo que a curva de gama escureceria.
    public void SetDim(double picture, bool linearLight, Rectangle bounds)
    {
        double keep = Math.Clamp(picture, 0, 1);
        if (linearLight) keep = Math.Pow(keep, 2.2);
        byte alpha = (byte)Math.Round((1 - keep) * 255);
        int half = bounds.Height / 2;
        upper.SetAlpha(alpha, new Rectangle(bounds.X, bounds.Y, bounds.Width, half));
        lower.SetAlpha(alpha, new Rectangle(bounds.X, bounds.Y + half, bounds.Width, bounds.Height - half));
    }

    public void Raise()
    {
        upper.Raise();
        lower.Raise();
    }

    public void Dispose()
    {
        upper.Dispose();
        lower.Dispose();
    }

    sealed class OverlayWindow : Form
    {
        Rectangle target;

        public OverlayWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            TopMost = true;
            Text = "Brilho overlay";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                    | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Prints e gravações mostram a imagem real, não a escurecida.
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED) return; // o tamanho é sempre o do monitor, em pixels
            if (m.Msg == Native.WM_NCHITTEST) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }

        public void SetAlpha(byte alpha, Rectangle bounds)
        {
            target = bounds;
            if (alpha == 0)
            {
                if (Visible) Hide();
                return;
            }
            Native.SetLayeredWindowAttributes(Handle, 0, alpha, Native.LWA_ALPHA);
            if (!Visible) Show();
            Raise();
        }

        /// Outras janelas "sempre no topo" (a barra de tarefas ao ser clicada)
        /// passam na frente; o app chama isto a cada segundo.
        public void Raise()
        {
            if (Visible)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, target.X, target.Y, target.Width, target.Height, Native.SWP_NOACTIVATE);
        }
    }
}
