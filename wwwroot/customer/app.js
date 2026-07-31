const form = document.getElementById("barcode-form");
const barcodeInput = document.getElementById("barcode");
const resultCard = document.getElementById("result");
const descriptionElement = document.getElementById("description");
const priceElement = document.getElementById("price");
const messageElement = document.getElementById("message");
const historyCard = document.getElementById("history-card");
const historyList = document.getElementById("history-list");

const cameraElement = document.getElementById("camera");
const cameraPlaceholder = document.getElementById("camera-placeholder");
const startCameraButton = document.getElementById("start-camera");
const stopCameraButton = document.getElementById("stop-camera");
const scanLine = document.querySelector(".scan-line");
const homeButton = document.getElementById("nav-home");
const historyButton = document.getElementById("nav-history");

const scannerCard =
    document.querySelector(".scanner-card");
let codeReader = null;
let cameraControls = null;
let lastScannedBarcode = "";
let lastScanTime = 0;
let productLoading = false;

form.addEventListener("submit", async (event) => {
    event.preventDefault();

    const barcode = barcodeInput.value.trim();

    if (!barcode) {
        showMessage("Inserisci un codice a barre.");
        return;
    }

    await searchProduct(barcode);
});

startCameraButton.addEventListener("click", startCamera);
stopCameraButton.addEventListener("click", stopCamera);

async function startCamera() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        showMessage(
            "Fotocamera non disponibile. Sul telefono sarà necessario usare HTTPS."
        );
        return;
    }

    if (typeof ZXing === "undefined") {
        showMessage("Impossibile caricare il lettore barcode.");
        return;
    }

    stopCamera();
    showMessage("Avvio fotocamera...");

    try {
        codeReader = new ZXing.BrowserMultiFormatReader();

       const videoInputDevices =
    await codeReader.listVideoInputDevices();
        if (!videoInputDevices.length) {
            showMessage("Nessuna fotocamera trovata.");
            return;
        }

        const preferredDevice =
            videoInputDevices.find(device =>
                /back|rear|environment|posteriore/i.test(device.label)
            ) ?? videoInputDevices[videoInputDevices.length - 1];

        cameraElement.style.display = "block";
        cameraPlaceholder.style.display = "none";
        scanLine.style.display = "block";

        startCameraButton.classList.add("hidden");
        stopCameraButton.classList.remove("hidden");

        cameraControls = await codeReader.decodeFromVideoDevice(
            preferredDevice.deviceId,
            cameraElement,
            async (result, error) => {
               if (result) {
    const barcode = result.getText().trim();

    // Non modificare il campo mentre l'utente sta scrivendo.
    if (document.activeElement === barcodeInput) {
        return;
    }

    // Accetta soltanto barcode numerici da 8, 12, 13 o 14 cifre.
    if (!/^(?:\d{8}|\d{12}|\d{13}|\d{14})$/.test(barcode)) {
        return;
    }

    barcodeInput.value = barcode;
    searchProduct(barcode);
}

                if (
                    error &&
                    !(error instanceof ZXing.NotFoundException)
                ) {
                    console.error("Errore lettura barcode:", error);
                }
            }
        );

        showMessage("Inquadra il codice a barre.");
    } catch (error) {
        console.error("Errore fotocamera:", error);
        stopCamera();

        if (error.name === "NotAllowedError") {
            showMessage("Permesso fotocamera negato.");
        } else if (error.name === "NotFoundError") {
            showMessage("Fotocamera non trovata.");
} else {
    const errorName = error?.name || "Errore sconosciuto";
    const errorMessage = error?.message || String(error);

    showMessage(`${errorName}: ${errorMessage}`);
}
    }
}

async function handleScannedBarcode(barcode) {
    const now = Date.now();

    if (
        productLoading ||
        (barcode === lastScannedBarcode &&
            now - lastScanTime < 3000)
    ) {
        return;
    }

    lastScannedBarcode = barcode;
    lastScanTime = now;

    barcodeInput.value = barcode;

    await searchProduct(barcode);
}

async function searchProduct(barcode) {
    if (productLoading) {
        return;
    }

    productLoading = true;
    resultCard.classList.add("hidden");
    showMessage("Ricerca prodotto...");

    try {
        const response = await fetch(
            `/api/public/product/${encodeURIComponent(barcode)}`,
            {
                headers: {
                    Accept: "application/json"
                }
            }
        );

        if (response.status === 404) {
            showMessage("Prodotto non trovato.");
            return;
        }

        if (!response.ok) {
            throw new Error(`Errore HTTP ${response.status}`);
        }

        const product = await response.json();

        descriptionElement.textContent =
            product.description || "Descrizione non disponibile";

        priceElement.textContent = formatPrice(product.price);

        resultCard.classList.remove("hidden");
saveToHistory(product, barcode);
        showMessage("Prodotto trovato.");
    } catch (error) {
        console.error(error);
        showMessage("Errore durante la ricerca del prodotto.");
    } finally {
        productLoading = false;
    }
}

function stopCamera() {
    if (cameraControls) {
        cameraControls.stop();
        cameraControls = null;
    }

    if (cameraElement.srcObject) {
        cameraElement.srcObject
            .getTracks()
            .forEach(track => track.stop());

        cameraElement.srcObject = null;
    }

    cameraElement.style.display = "none";
    cameraPlaceholder.style.display = "flex";
    scanLine.style.display = "none";

    startCameraButton.classList.remove("hidden");
    stopCameraButton.classList.add("hidden");

    codeReader = null;
}

function formatPrice(value) {
    const price = Number(value);

    if (!Number.isFinite(price)) {
        return "Prezzo non disponibile";
    }

    return new Intl.NumberFormat("it-IT", {
        style: "currency",
        currency: "EUR"
    }).format(price);
}

function showMessage(message) {
    messageElement.textContent = message;
}

window.addEventListener("beforeunload", stopCamera);

function saveToHistory(product, barcode) {

    let history =
        JSON.parse(localStorage.getItem("history") || "[]");

    history = history.filter(
        item => item.barcode !== barcode
    );

    history.unshift({
        barcode: barcode,
        description: product.description,
        price: product.price
    });

    history = history.slice(0, 10);

    localStorage.setItem(
        "history",
        JSON.stringify(history)
    );

    renderHistory();
}

function renderHistory() {

    const history =
        JSON.parse(localStorage.getItem("history") || "[]");

    if (history.length === 0) {
        historyCard.classList.add("hidden");
        return;
    }

    historyCard.classList.remove("hidden");

    historyList.innerHTML = history
        .map(item => `
            <div class="history-item"
                 onclick="searchProduct('${item.barcode}')">

                <div>${item.description}</div>

                <strong>${formatPrice(item.price)}</strong>

            </div>
        `)
        .join("");
}

renderHistory();

homeButton.addEventListener("click", () => {

    scannerCard.classList.remove("hidden");

    resultCard.classList.remove("hidden");

    historyCard.classList.add("hidden");

    homeButton.classList.add("active");

    historyButton.classList.remove("active");
});

historyButton.addEventListener("click", () => {

    scannerCard.classList.add("hidden");

    resultCard.classList.add("hidden");

    historyCard.classList.remove("hidden");

    historyButton.classList.add("active");

    homeButton.classList.remove("active");
});