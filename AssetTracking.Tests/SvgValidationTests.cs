using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace AssetTracking.Tests
{
    public class SvgValidationTests
    {
        private static (bool IsValid, string? ErrorMessage) ValidateAndSanitizeSvg(string svgText)
        {
            if (string.IsNullOrWhiteSpace(svgText))
            {
                return (false, "Uploaded SVG file is empty.");
            }

            string lower = svgText.ToLowerInvariant();

            // 1. Reject script tags
            if (lower.Contains("<script") || lower.Contains("</script>"))
            {
                return (false, "Unsafe SVG file detected: SVG contains script tags.");
            }

            // 2. Reject foreignObject
            if (lower.Contains("<foreignobject") || lower.Contains("</foreignobject>"))
            {
                return (false, "Unsafe SVG file detected: SVG contains foreignObject elements.");
            }

            // 3. Reject javascript: URIs
            if (lower.Contains("javascript:"))
            {
                return (false, "Unsafe SVG file detected: SVG contains javascript: execution URIs.");
            }

            // 4. Reject inline event handlers (on[a-z]+=, e.g. onload=, onerror=, onclick=, etc.)
            if (Regex.IsMatch(lower, @"\bon[a-z]+\s*=", RegexOptions.IgnoreCase))
            {
                return (false, "Unsafe SVG file detected: SVG contains inline event handler attributes.");
            }

            // 5. Reject external resource references (http://, https://)
            if (Regex.IsMatch(lower, @"(href|src|xlink:href)\s*=\s*[""']?\s*https?://", RegexOptions.IgnoreCase))
            {
                return (false, "Unsafe SVG file detected: SVG contains external resource references.");
            }

            return (true, null);
        }

        [Fact]
        public void ValidSvg_ShouldPassValidation()
        {
            string validSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 100 100"">
  <rect x=""10"" y=""10"" width=""80"" height=""80"" fill=""#3b82f6"" />
  <circle cx=""50"" cy=""50"" r=""20"" fill=""#ffffff"" />
</svg>";

            var (isValid, errorMessage) = ValidateAndSanitizeSvg(validSvg);

            Assert.True(isValid);
            Assert.Null(errorMessage);
        }

        [Fact]
        public void SvgWithScriptTag_ShouldFailValidation()
        {
            string maliciousSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"">
  <script type=""text/javascript"">alert('XSS');</script>
  <rect x=""0"" y=""0"" width=""10"" height=""10"" />
</svg>";

            var (isValid, errorMessage) = ValidateAndSanitizeSvg(maliciousSvg);

            Assert.False(isValid);
            Assert.Contains("script tags", errorMessage);
        }

        [Fact]
        public void SvgWithOnLoadAttribute_ShouldFailValidation()
        {
            string maliciousSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"" onload=""alert('XSS')"">
  <circle cx=""5"" cy=""5"" r=""5"" />
</svg>";

            var (isValid, errorMessage) = ValidateAndSanitizeSvg(maliciousSvg);

            Assert.False(isValid);
            Assert.Contains("event handler", errorMessage);
        }

        [Fact]
        public void SvgWithForeignObject_ShouldFailValidation()
        {
            string maliciousSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"">
  <foreignObject width=""100"" height=""100"">
    <iframe src=""https://example.com""></iframe>
  </foreignObject>
</svg>";

            var (isValid, errorMessage) = ValidateAndSanitizeSvg(maliciousSvg);

            Assert.False(isValid);
            Assert.Contains("foreignObject", errorMessage);
        }

        [Fact]
        public void SvgWithExternalResource_ShouldFailValidation()
        {
            string maliciousSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"">
  <image href=""https://malicious.com/image.png"" width=""100"" height=""100"" />
</svg>";

            var (isValid, errorMessage) = ValidateAndSanitizeSvg(maliciousSvg);

            Assert.False(isValid);
            Assert.Contains("external resource", errorMessage);
        }
    }
}
