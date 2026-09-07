# Watermark editor

Open the service root (`/`) to use the built-in browser editor. It is intended for a private operator network and requires the same bearer token as the API when `INACTIVEPDF_API_TOKEN` is configured.

## Workflow

1. Select an existing profile or choose **New profile**.
2. Choose **Text** or **Image**. Image profiles use an asset filename, never an arbitrary filesystem path.
3. Set font, color, opacity, rotation, position, layer, page selection (`No pages`, `All pages`, first/last/odd/even, or a validated custom range), optional width/height, tiling, header, footer, and page numbering.
4. Confirm the live first-page preview, then save the profile.
5. Use the saved name as `watermarkProfile` in a conversion request, or send the same fields as a direct `watermark` object.

## Preview and production behavior

The preview is a fast browser approximation. It shows the selected position, rotation, opacity, layer, page furniture, page range, and tiled layout. **Over content** draws the watermark above document content; **Behind content** draws it first so document content remains visually dominant. Tiling repeats the watermark in a grid on every selected page; it is independent of page selection. `{page}` and `{pages}` are replaced in headers, footers, and the page-number format.

The conversion worker applies the settings to every selected PDF page. Image opacity is applied to the image alpha channel before embedding; when only width or height is specified, the other dimension is calculated from the source aspect ratio. Rotation is applied around the watermark center. Invalid page ranges, dimensions, opacity, rotation, colors, profile names, assets, and workspace paths are rejected.

Image profiles use the protected asset selector. Use **Browse and upload** to add a PNG, JPEG, GIF, WEBP, or BMP asset; uploads are stored beneath `INACTIVEPDF_WATERMARK_ASSET_PATH`, written atomically, and never treated as arbitrary filesystem paths. The asset API is also available at `GET /v1/watermark-assets`, `GET /v1/watermark-assets/{name}`, and `POST /v1/watermark-assets` with multipart field `file`.

For production, render representative outputs with the target PDF viewer and compare margins, fonts, image transparency, rotation, page ranges, headers, footers, and tiled placements.
