// pdf-viewer.js (ES module)
// Self-contained PDF viewer — loads pdf.js internally, no external wiring needed.

const PDFJS_VERSION = "5.4.149";
const PDFJS_CDN = `https://cdn.jsdelivr.net/npm/pdfjs-dist@${PDFJS_VERSION}`;

const ZOOM_STEP = 0.2;
const ZOOM_MIN = 0.25;
const ZOOM_MAX = 5;
const RESIZE_DEBOUNCE_MS = 200;

/**
 * Initialise the PDF viewer.
 * @param {{ url: string }} config — must contain the PDF source URL.
 */
export async function initPdfViewer(config) {
    if (!config?.url) {
        console.error("PDF viewer: missing config.url");
        return;
    }

    // Razor HTML-encodes `&` ? `&amp;` inside attribute/JS strings; fix that.
    if (config.url.includes("&amp;")) {
        config.url = config.url.replace(/&amp;/g, "&");
    }

    const canvas = document.getElementById("pdfCanvas");
    if (!canvas) {
        console.error("PDF viewer: #pdfCanvas not found");
        return;
    }
    const ctx = canvas.getContext("2d");

    const container  = document.getElementById("pdfContainer");
    const btnPrev    = document.getElementById("btnPrev");
    const btnNext    = document.getElementById("btnNext");
    const btnZoomIn  = document.getElementById("btnZoomIn");
    const btnZoomOut = document.getElementById("btnZoomOut");
    const btnFitWidth = document.getElementById("btnFitWidth");
    const btnPrint   = document.getElementById("btnPrint");
    const pageNumSpan   = document.getElementById("pageNum");
    const pageCountSpan = document.getElementById("pageCount");
    const zoomLevelSpan = document.getElementById("zoomLevel");

    let pdfDoc = null;
    let currentPage = 1;
    let scale = 1.5;
    let fitWidthMode = false;
    let activeRenderTask = null;

    showLoading(true);

    try {
        const pdfjsLib = await import(`${PDFJS_CDN}/+esm`);
        pdfjsLib.GlobalWorkerOptions.workerSrc = `${PDFJS_CDN}/build/pdf.worker.mjs`;

        pdfDoc = await pdfjsLib.getDocument({
            url: config.url,
            withCredentials: false,
        }).promise;

        pageCountSpan.textContent = pdfDoc.numPages.toString();
        enableControls();

        fitWidthMode = true;
        await renderPage(currentPage);
    } catch (err) {
        console.error("PDF viewer — load failed:", err);
        showError(err);
        return;
    } finally {
        showLoading(false);
    }

    // ?? Rendering ???????????????????????????????????????????????

    async function renderPage(num) {
        if (activeRenderTask) {
            activeRenderTask.cancel();
            activeRenderTask = null;
        }

        const page = await pdfDoc.getPage(num);
        const dpr = window.devicePixelRatio || 1;

        let effectiveScale = scale;
        if (fitWidthMode) {
            const unscaled = page.getViewport({ scale: 1 });
            effectiveScale = (container.clientWidth - 24) / unscaled.width;
            scale = effectiveScale;
        }

        const viewport = page.getViewport({ scale: effectiveScale });

        canvas.width  = Math.floor(viewport.width * dpr);
        canvas.height = Math.floor(viewport.height * dpr);
        canvas.style.width  = `${Math.floor(viewport.width)}px`;
        canvas.style.height = `${Math.floor(viewport.height)}px`;

        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        const renderTask = page.render({ canvasContext: ctx, viewport });
        activeRenderTask = renderTask;

        try {
            await renderTask.promise;
        } catch (err) {
            if (err?.name === "RenderingCancelledException") return;
            throw err;
        } finally {
            if (activeRenderTask === renderTask) activeRenderTask = null;
        }

        pageNumSpan.textContent = num.toString();
        updateZoomDisplay(effectiveScale);
        updateNavButtons();
    }

    // ?? Controls ????????????????????????????????????????????????

    function enableControls() {
        [btnPrev, btnNext, btnZoomIn, btnZoomOut, btnFitWidth, btnPrint]
            .forEach(b => { if (b) b.disabled = false; });
    }

    function updateNavButtons() {
        if (btnPrev) btnPrev.disabled = currentPage <= 1;
        if (btnNext) btnNext.disabled = currentPage >= pdfDoc.numPages;
    }

    function updateZoomDisplay(s) {
        if (zoomLevelSpan) zoomLevelSpan.textContent = `${Math.round(s * 100)}%`;
    }

    async function goToPage(num) {
        num = Math.max(1, Math.min(num, pdfDoc.numPages));
        if (num === currentPage) return;
        currentPage = num;
        await renderPage(currentPage);
    }

    async function zoom(delta) {
        fitWidthMode = false;
        scale = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, scale + delta));
        await renderPage(currentPage);
    }

    async function fitWidth() {
        fitWidthMode = true;
        await renderPage(currentPage);
    }

    btnPrev?.addEventListener("click", () => goToPage(currentPage - 1));
    btnNext?.addEventListener("click", () => goToPage(currentPage + 1));
    btnZoomIn?.addEventListener("click", () => zoom(ZOOM_STEP));
    btnZoomOut?.addEventListener("click", () => zoom(-ZOOM_STEP));
    btnFitWidth?.addEventListener("click", () => fitWidth());
    btnPrint?.addEventListener("click", () => window.open(config.url, "_blank", "noopener"));

    // ?? Keyboard shortcuts ??????????????????????????????????????

    document.addEventListener("keydown", (e) => {
        if (!pdfDoc) return;
        const target = e.target;
        if (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable) return;

        switch (e.key) {
            case "ArrowLeft":
            case "ArrowUp":
                e.preventDefault();
                goToPage(currentPage - 1);
                break;
            case "ArrowRight":
            case "ArrowDown":
                e.preventDefault();
                goToPage(currentPage + 1);
                break;
            case "+":
            case "=":
                e.preventDefault();
                zoom(ZOOM_STEP);
                break;
            case "-":
                e.preventDefault();
                zoom(-ZOOM_STEP);
                break;
        }
    });

    // ?? Responsive resize (fit-width mode only) ?????????????????

    let resizeTimer = null;
    window.addEventListener("resize", () => {
        if (!fitWidthMode || !pdfDoc) return;
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => renderPage(currentPage), RESIZE_DEBOUNCE_MS);
    });

    // ?? Helpers ?????????????????????????????????????????????????

    function showLoading(show) {
        let spinner = container?.querySelector(".vr-pdf-loading");
        if (show && container) {
            if (!spinner) {
                spinner = document.createElement("div");
                spinner.className = "vr-pdf-loading";
                spinner.innerHTML = `
                    <div class="vr-pdf-spinner"></div>
                    <span>Loading document…</span>`;
                container.prepend(spinner);
            }
            canvas.style.display = "none";
        } else {
            spinner?.remove();
            if (canvas) canvas.style.display = "";
        }
    }

    function showError(err) {
        if (!container) return;
        const msg = err?.name === "MissingPDFException"
            ? "PDF file not found. The link may have expired."
            : err?.name === "UnknownErrorException" && err?.message?.includes("Failed to fetch")
                ? "Could not fetch the PDF. The SAS link may have expired — try reloading the page."
                : "Failed to load the document.";

        container.innerHTML = `
            <div class="vr-pdf-error">
                <svg viewBox="0 0 24 24" width="32" height="32" stroke="currentColor" fill="none"
                     stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
                    <circle cx="12" cy="12" r="10"/><line x1="15" y1="9" x2="9" y2="15"/>
                    <line x1="9" y1="9" x2="15" y2="15"/>
                </svg>
                <p>${msg}</p>
                <button onclick="location.reload()" class="btn btn-sm btn-outline-primary mt-2">Reload</button>
            </div>`;
    }
}