namespace twoSaaSCore.Services
{
    public class AzureAiOptions
    {
        /// <summary>Azure OpenAI endpoint URL (e.g. https://my-resource.openai.azure.com/).</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>Chat model deployment name (e.g. gpt-4o).</summary>
        public string ChatModel { get; set; } = "gpt-4o";

        /// <summary>Optional tenant ID for local/dev credential resolution.</summary>
        public string? TenantId { get; set; }

        /// <summary>When true, skips Managed Identity in Development to avoid local Arc token issues.</summary>
        public bool ExcludeManagedIdentityInDevelopment { get; set; } = true;

        /// <summary>Enable OCR fallback for PDFs before indexing into vector store.</summary>
        public bool EnablePdfOcrFallback { get; set; }

        /// <summary>Azure Document Intelligence endpoint for OCR (e.g. https://my-docintel.cognitiveservices.azure.com/).</summary>
        public string? OcrEndpoint { get; set; }

        /// <summary>Azure Document Intelligence API key for OCR.</summary>
        public string? OcrApiKey { get; set; }

        /// <summary>Document Intelligence model ID for OCR.</summary>
        public string OcrModel { get; set; } = "prebuilt-read";
    }
}
