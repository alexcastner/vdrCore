// pdf-viewer.js (ES module)
// Exports an initialization function; no reliance on global pdfjsLib or window config.

export async function initPdfViewer(config, pdfjsLib) {
    if (!config?.url) {
        console.error("PDF viewer: missing config.url");
        return;
    }

    if (config.url.includes("&amp;")) {
        config.url = config.url.replace(/&amp;/g, "&");
    }

    const canvas = document.getElementById("pdfCanvas");
    const ctx = canvas.getContext("2d");

    const btnPrev = document.getElementById("btnPrev");
    const btnNext = document.getElementById("btnNext");
    const btnZoomIn = document.getElementById("btnZoomIn");
    const btnZoomOut = document.getElementById("btnZoomOut");
    const btnFitWidth = document.getElementById("btnFitWidth");
    const btnPrint = document.getElementById("btnPrint");
    const pageNumSpan = document.getElementById("pageNum");
    const pageCountSpan = document.getElementById("pageCount");
    const container = document.getElementById("pdfContainer");

    let pdfDoc = null;
    let currentPage = 1;
    let scale = 1.2;
    let rendering = false;
    let pendingPage = null;
    let lastFitWidth = false;

    try {
        pdfDoc = await pdfjsLib.getDocument(config.url).promise;
        pageCountSpan.textContent = pdfDoc.numPages.toString();
        enableControls();
        await queueRenderPage(1);
    } catch (e) {
        console.error("Failed to load PDF:", e);
        container.innerHTML = "<div class='text-danger p-3'>Failed to load PDF.</div>";
        return;
    }

    function enableControls() {
        [btnPrev, btnNext, btnZoomIn, btnZoomOut, btnFitWidth, btnPrint]
            .forEach(b => b && (b.disabled = false));
    }

    async function renderPage(num) {
        rendering = true;
        const page = await pdfDoc.getPage(num);

        // Auto-fit width only if last action was Fit Width
        let effectiveScale = scale;
        if (lastFitWidth) {
            const unscaledViewport = page.getViewport({ scale: 1 });
            const available = container.clientWidth - 20;
            effectiveScale = available / unscaledViewport.width;
        }

        const viewport = page.getViewport({ scale: effectiveScale });
        canvas.height = Math.floor(viewport.height);
        canvas.width = Math.floor(viewport.width);

        const renderCtx = { canvasContext: ctx, viewport };
        await page.render(renderCtx).promise;

        if (!lastFitWidth) {
            scale = effectiveScale; // keep explicit scale when not fit-width mode
        }

        rendering = false;
        if (pendingPage !== null) {
            const p = pendingPage;
            pendingPage = null;
            renderPage(p);
        }
        pageNumSpan.textContent = num.toString();
    }

    async function queueRenderPage(num) {
        if (rendering) {
            pendingPage = num;
        } else {
            await renderPage(num);
        }
    }

    btnPrev?.addEventListener("click", async () => {
        if (currentPage <= 1) return;
        currentPage--;
        await queueRenderPage(currentPage);
    });

    btnNext?.addEventListener("click", async () => {
        if (currentPage >= pdfDoc.numPages) return;
        currentPage++;
        await queueRenderPage(currentPage);
    });

    btnZoomIn?.addEventListener("click", async () => {
        lastFitWidth = false;
        scale = Math.min(scale + 0.15, 6);
        await queueRenderPage(currentPage);
    });

    btnZoomOut?.addEventListener("click", async () => {
        lastFitWidth = false;
        scale = Math.max(scale - 0.15, 0.25);
        await queueRenderPage(currentPage);
    });

    btnFitWidth?.addEventListener("click", async () => {
        lastFitWidth = true;
        await queueRenderPage(currentPage);
    });

    btnPrint?.addEventListener("click", () => {
        // Basic print strategy; a more advanced implementation could render to an iframe
        window.open(config.url, "_blank", "noopener");
    });

    // Re-render if container size changes and we are in fit-width mode
    let resizeTimer = null;
    window.addEventListener("resize", () => {
        if (!lastFitWidth) return;
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => queueRenderPage(currentPage), 150);
    });
}