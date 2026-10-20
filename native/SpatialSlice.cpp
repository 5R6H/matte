// SpatialSlice.dll: the CPU Gaussian-splat renderer the SHARP generator uses to cut a scene into depth slices
// (ddgs_cpu from Spatial Photos, MIT, in native\ddgs). Built here so a user's machine needs no C++ compiler; the
// generator (spatial\ddgs.py) calls it through ctypes with plain float arrays.
#include "ddgs/ddgs_cpu_forward.cpp"

extern "C" __declspec(dllexport) unsigned long long __stdcall DdgsRender(
	unsigned width, unsigned height, const float* viewRowMajor, const float* projRowMajor, float focalX, float focalY,
	unsigned count, const float* means, const float* scales, const float* rotationsXyzw, const float* opacities, const float* rgb,
	unsigned threads, float* outColor, float* outAlpha, float* outDepth)
{
	if(!width || !height || !viewRowMajor || !projRowMajor || !outColor || !outAlpha || !outDepth) return 0;
	DDGSCPUsettings s;
	s.width = width; s.height = height;
	s.view = dc_mat4_load_row_major(viewRowMajor);
	s.proj = dc_mat4_load_row_major(projRowMajor);
	s.focalX = focalX; s.focalY = focalY;
	s.debug = false; s.numThreads = threads;
	DDGSCPUgaussians g;
	g.count = count; g.means = means; g.scales = scales; g.rotations = rotationsXyzw; g.opacities = opacities; g.harmonics = rgb;
	return ddgs_cpu_forward(s, g, outColor, outAlpha, outDepth);
}
