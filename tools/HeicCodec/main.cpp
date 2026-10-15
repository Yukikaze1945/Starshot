// SPDX-License-Identifier: GPL-3.0-or-later
// Standalone codec worker. Starshot communicates through bounded binary pipes.
#include <libheif/heif.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include <fcntl.h>
#include <io.h>

static constexpr uint64_t maxPixels = 64000000;
static void check(heif_error e) { if (e.code != heif_error_Ok) throw std::runtime_error(e.message ? e.message : "HEIF error"); }
template<class T, auto Free> using owner = std::unique_ptr<T, decltype(Free)>;
static void read(void* data, size_t bytes) {
    if (bytes && !std::cin.read(static_cast<char*>(data), bytes)) throw std::runtime_error("Truncated pixel input");
}
static void dimensions(int w, int h) {
    if (w <= 0 || h <= 0 || uint64_t(w) * h > maxPixels) throw std::runtime_error("Image exceeds 64 MP limit");
}
static void limits(heif_context* context) {
    if (!context) throw std::bad_alloc();
    auto* limits = heif_context_get_security_limits(context);
    limits->max_image_size_pixels = maxPixels;
    limits->max_memory_block_size = 512ull * 1024 * 1024;
    limits->max_total_memory = 1024ull * 1024 * 1024;
    heif_context_set_max_decoding_threads(context, 2);
}
static void encode(int argc, wchar_t** argv) {
    if (argc != 10) throw std::runtime_error("encode width height bgra8|rgba8|rgba16 quality hdr maxCLL maxFALL xmpBytes");
    int w = std::stoi(argv[2]), h = std::stoi(argv[3]), quality = std::stoi(argv[5]);
    dimensions(w, h);
    int mode = std::stoi(argv[6]);
    bool hdr = mode == 1, wide = std::wstring(argv[4]) == L"rgba16", bgra = std::wstring(argv[4]) == L"bgra8";
    if (hdr && !wide) throw std::runtime_error("HDR requires PQ RGB16 input");
    int xmpBytes = std::stoi(argv[9]);
    if (quality < 1 || quality > 100 || xmpBytes < 0 || xmpBytes > 1048576) throw std::runtime_error("Invalid encoder options");
    owner<heif_context, heif_context_free> context(heif_context_alloc(), heif_context_free); limits(context.get());
    heif_encoder* rawEncoder = nullptr;
    check(heif_context_get_encoder_for_format(context.get(), heif_compression_HEVC, &rawEncoder));
    owner<heif_encoder, heif_encoder_release> encoder(rawEncoder, heif_encoder_release);
    check(heif_encoder_set_lossy_quality(encoder.get(), quality));
    check(heif_encoder_set_logging_level(encoder.get(), 0));
    check(heif_encoder_set_parameter_string(encoder.get(), "preset", "medium"));
    check(heif_encoder_set_parameter_string(encoder.get(), "chroma", "420"));
    check(heif_encoder_set_parameter(encoder.get(), "x265:pools", "2"));
    check(heif_encoder_set_parameter(encoder.get(), "x265:frame-threads", "1"));
    heif_image* rawImage = nullptr;
    check(heif_image_create(w, h, heif_colorspace_RGB, heif_chroma_interleaved_RRGGBB_LE, &rawImage));
    owner<heif_image, heif_image_release> image(rawImage, heif_image_release);
    check(heif_image_add_plane(image.get(), heif_channel_interleaved, w, h, 10));
    int stride = 0; uint8_t* pixels = heif_image_get_plane(image.get(), heif_channel_interleaved, &stride);
    std::vector<uint8_t> row(size_t(w) * (wide ? 8 : 4));
    for (int y = 0; y < h; ++y) {
        read(row.data(), row.size()); auto* output = reinterpret_cast<uint16_t*>(pixels + size_t(y) * stride);
        for (int x = 0; x < w; ++x) for (int c = 0; c < 3; ++c) {
            uint32_t value;
            if (wide) { size_t i = size_t(x) * 8 + c * 2; value = (row[i] | uint32_t(row[i + 1]) << 8); }
            else value = uint32_t(row[size_t(x) * 4 + (bgra ? 2 - c : c)]) * 257;
            output[x * 3 + c] = uint16_t((value * 1023 + 32767) / 65535);
        }
    }
    owner<heif_color_profile_nclx, heif_nclx_color_profile_free> nclx(heif_nclx_color_profile_alloc(), heif_nclx_color_profile_free);
    check(heif_nclx_color_profile_set_color_primaries(nclx.get(), hdr ? 9 : mode == 2 ? 12 : 1));
    check(heif_nclx_color_profile_set_transfer_characteristics(nclx.get(), hdr ? 16 : 13));
    // Main10 / 4:2:0 remains compatible with ordinary phone image decoders.
    // Quality 100 is maximum quality, not a promise of RGB-lossless subsampling.
    check(heif_nclx_color_profile_set_matrix_coefficients(nclx.get(), hdr ? 9 : 1));
    nclx->full_range_flag = 1; check(heif_image_set_nclx_color_profile(image.get(), nclx.get()));
    if (hdr) {
        heif_content_light_level clli{};
        clli.max_content_light_level = uint16_t(std::clamp(std::stoi(argv[7]), 0, 65535));
        clli.max_pic_average_light_level = uint16_t(std::clamp(std::stoi(argv[8]), 0, 65535));
        if (clli.max_content_light_level || clli.max_pic_average_light_level) heif_image_set_content_light_level(image.get(), &clli);
    }
    owner<heif_encoding_options, heif_encoding_options_free> options(heif_encoding_options_alloc(), heif_encoding_options_free);
    options->save_alpha_channel = 0; options->output_nclx_profile = nclx.get();
    heif_image_handle* rawHandle = nullptr;
    check(heif_context_encode_image(context.get(), image.get(), encoder.get(), options.get(), &rawHandle));
    owner<heif_image_handle, heif_image_handle_release> handle(rawHandle, heif_image_handle_release);
    std::vector<uint8_t> xmp(xmpBytes); read(xmp.data(), xmp.size());
    if (!xmp.empty()) check(heif_context_add_XMP_metadata(context.get(), handle.get(), xmp.data(), int(xmp.size())));
    heif_writer writer{ 1, [](heif_context*, const void* data, size_t size, void*) -> heif_error {
        std::cout.write(static_cast<const char*>(data), size);
        return std::cout ? heif_error{heif_error_Ok, heif_suberror_Unspecified, nullptr}
                         : heif_error{heif_error_Encoding_error, heif_suberror_Unspecified, "Output pipe failed"};
    }};
    check(heif_context_write(context.get(), &writer, nullptr));
}
static void decode(int argc, wchar_t** argv) {
    bool probe = std::wstring(argv[1]) == L"--size";
    if (argc != (probe ? 3 : 4)) throw std::runtime_error("decode file maxDimension (0 = original)");
    int maximum = probe ? 0 : std::stoi(argv[3]);
    if (maximum < 0 || maximum > 8192) throw std::runtime_error("Invalid thumbnail bound");
    std::ifstream file(std::filesystem::path(argv[2]), std::ios::binary | std::ios::ate);
    if (!file || file.tellg() <= 0 || file.tellg() > 512ll * 1024 * 1024) throw std::runtime_error("Invalid HEIC input size");
    std::vector<uint8_t> input(static_cast<size_t>(file.tellg())); file.seekg(0); file.read(reinterpret_cast<char*>(input.data()), input.size());
    if (!file) throw std::runtime_error("Cannot read HEIC");
    owner<heif_context, heif_context_free> context(heif_context_alloc(), heif_context_free); limits(context.get());
    check(heif_context_read_from_memory_without_copy(context.get(), input.data(), input.size(), nullptr));
    heif_image_handle* rawHandle = nullptr;
    check(heif_context_get_primary_image_handle(context.get(), &rawHandle));
    owner<heif_image_handle, heif_image_handle_release> handle(rawHandle, heif_image_handle_release);
    dimensions(heif_image_handle_get_width(handle.get()), heif_image_handle_get_height(handle.get()));
    if (probe) { std::cout << heif_image_handle_get_width(handle.get()) << ' ' << heif_image_handle_get_height(handle.get()); return; }
    owner<heif_decoding_options, heif_decoding_options_free> options(heif_decoding_options_alloc(), heif_decoding_options_free);
    options->output_image_nclx_profile_passthrough = 1; // Never silently convert PQ to sRGB.
    options->num_codec_threads = 2;
    heif_image* rawImage = nullptr;
    check(heif_decode_image(handle.get(), &rawImage, heif_colorspace_RGB, heif_chroma_interleaved_RRGGBBAA_LE, options.get()));
    owner<heif_image, heif_image_release> image(rawImage, heif_image_release);
    int w = heif_image_get_width(image.get(), heif_channel_interleaved), h = heif_image_get_height(image.get(), heif_channel_interleaved);
    if (maximum > 0 && std::max(w, h) > maximum) {
        double scale = double(maximum) / std::max(w, h); heif_image* scaled = nullptr;
        check(heif_image_scale_image(image.get(), &scaled, std::max(1, int(w * scale)), std::max(1, int(h * scale)), nullptr));
        image.reset(scaled); w = heif_image_get_width(image.get(), heif_channel_interleaved); h = heif_image_get_height(image.get(), heif_channel_interleaved);
    }
    heif_color_profile_nclx* rawNclx = nullptr;
    heif_image_handle_get_nclx_color_profile(handle.get(), &rawNclx);
    owner<heif_color_profile_nclx, heif_nclx_color_profile_free> color(rawNclx, heif_nclx_color_profile_free);
    size_t iccSize = heif_image_handle_get_raw_color_profile_size(handle.get());
    if (iccSize > 1048576) throw std::runtime_error("ICC profile too large");
    std::vector<uint8_t> icc(iccSize); if (iccSize) check(heif_image_handle_get_raw_color_profile(handle.get(), icc.data()));
    int bits = heif_image_get_bits_per_pixel_range(image.get(), heif_channel_interleaved);
    if (bits < 8 || bits > 16) throw std::runtime_error("Unsupported pixel precision");
    heif_content_light_level clli{}; heif_image_handle_get_content_light_level(handle.get(), &clli);
    std::array<uint32_t, 9> header{0x31434948u, uint32_t(w), uint32_t(h), color ? uint32_t(color->color_primaries) : 1u,
        color ? uint32_t(color->transfer_characteristics) : 13u, uint32_t(bits), uint32_t(iccSize), clli.max_content_light_level, clli.max_pic_average_light_level};
    std::cout.write(reinterpret_cast<const char*>(header.data()), sizeof(header));
    std::cout.write(reinterpret_cast<const char*>(icc.data()), icc.size());
    int stride = 0; const uint8_t* pixels = heif_image_get_plane_readonly(image.get(), heif_channel_interleaved, &stride);
    std::vector<uint16_t> row(size_t(w) * 4); uint32_t max = (1u << bits) - 1;
    for (int y = 0; y < h; ++y) {
        const auto* source = reinterpret_cast<const uint16_t*>(pixels + size_t(y) * stride);
        for (size_t x = 0; x < row.size(); ++x) row[x] = uint16_t((uint32_t(source[x]) * 65535 + max / 2) / max);
        std::cout.write(reinterpret_cast<const char*>(row.data()), row.size() * 2);
    }
    if (!std::cout) throw std::runtime_error("Output pipe failed");
}
int wmain(int argc, wchar_t** argv) {
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
    try {
        if (argc == 2 && std::wstring(argv[1]) == L"--version") {
            std::cout << "StarshotHeic/1 libheif/" << heif_get_version() << " hevcEncoder=" << heif_have_encoder_for_format(heif_compression_HEVC) << " hevcDecoder=" << heif_have_decoder_for_format(heif_compression_HEVC) << '\n'; return 0;
        }
        if (argc > 1 && std::wstring(argv[1]) == L"--encode") encode(argc, argv);
        else if (argc > 1 && (std::wstring(argv[1]) == L"--decode" || std::wstring(argv[1]) == L"--size")) decode(argc, argv);
        else throw std::runtime_error("Unknown command");
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
