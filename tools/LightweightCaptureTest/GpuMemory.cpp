#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <d3dkmthk.h>
#include <stdint.h>

struct Sample {
    uint64_t dedicatedResident, sharedResident, dedicatedCommitted, sharedCommitted;
    uint32_t adapters, residentQueries, committedQueries, failedQueries;
    uint32_t enumeratedAdapters, firstFailure;
};

// Same KMT counters as System Informer's resident and committed columns.
// This code only queries existing allocations; it creates no D3D device.
extern "C" __declspec(dllexport) DWORD ReadGpuMemory(DWORD pid, Sample* sample) {
    if (!sample) return ERROR_INVALID_PARAMETER;
    *sample = {};
    HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, pid);
    if (!process) return GetLastError();
    D3DKMT_ADAPTERINFO adapters[16]{};
    D3DKMT_ENUMADAPTERS2 enumeration{};
    enumeration.NumAdapters = 16; enumeration.pAdapters = adapters;
    NTSTATUS status = D3DKMTEnumAdapters2(&enumeration);
    if (status < 0) { CloseHandle(process); return static_cast<DWORD>(status); }
    sample->enumeratedAdapters = enumeration.NumAdapters;
    auto failed = [&](NTSTATUS result) { ++sample->failedQueries; if (!sample->firstFailure) sample->firstFailure = static_cast<uint32_t>(result); };
    for (ULONG i = 0; i < enumeration.NumAdapters; ++i) {
        auto luid = adapters[i].AdapterLuid;
        D3DKMT_QUERYSTATISTICS adapter{};
        adapter.Type = D3DKMT_QUERYSTATISTICS_ADAPTER; adapter.AdapterLuid = luid;
        NTSTATUS adapterStatus = D3DKMTQueryStatistics(&adapter);
        if (adapterStatus >= 0) {
            ++sample->adapters;
            for (ULONG group = 0; group < 2; ++group) {
                D3DKMT_QUERYSTATISTICS query{};
                query.Type = D3DKMT_QUERYSTATISTICS_PROCESS_SEGMENT_GROUP;
                query.AdapterLuid = luid; query.hProcess = process;
                query.QueryProcessSegmentGroup = static_cast<D3DKMT_MEMORY_SEGMENT_GROUP>(group);
                NTSTATUS queryStatus = D3DKMTQueryStatistics(&query);
                if (queryStatus >= 0) {
                    ++sample->residentQueries;
                    auto bytes = query.QueryResult.ProcessSegmentGroupInformation.Usage;
                    if (group == 0) sample->dedicatedResident += bytes;
                    else sample->sharedResident += bytes;
                } else failed(queryStatus);
            }
            for (ULONG segment = 0; segment < adapter.QueryResult.AdapterInformation.NbSegments; ++segment) {
                D3DKMT_QUERYSTATISTICS info{};
                info.Type = D3DKMT_QUERYSTATISTICS_SEGMENT; info.AdapterLuid = luid;
                info.QuerySegment.SegmentId = segment;
                NTSTATUS segmentStatus = D3DKMTQueryStatistics(&info);
                if (segmentStatus < 0) { failed(segmentStatus); continue; }
                bool shared = info.QueryResult.SegmentInformation.Aperture != 0;
                D3DKMT_QUERYSTATISTICS query{};
                query.Type = D3DKMT_QUERYSTATISTICS_PROCESS_SEGMENT;
                query.AdapterLuid = luid; query.hProcess = process; query.QueryProcessSegment.SegmentId = segment;
                NTSTATUS queryStatus = D3DKMTQueryStatistics(&query);
                if (queryStatus >= 0) {
                    ++sample->committedQueries;
                    auto bytes = query.QueryResult.ProcessSegmentInformation.BytesCommitted;
                    if (shared) sample->sharedCommitted += bytes;
                    else sample->dedicatedCommitted += bytes;
                } else failed(queryStatus);
            }
        } else failed(adapterStatus);
        D3DKMT_CLOSEADAPTER close{}; close.hAdapter = adapters[i].hAdapter;
        D3DKMTCloseAdapter(&close);
    }
    CloseHandle(process); return ERROR_SUCCESS;
}
