using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
#if ONEJS_VECTORGRAPHICS
using Unity.VectorGraphics;

namespace OneJS {
    /// <summary>
    /// Utility for loading SVG content as VectorImage at runtime.
    /// Used by JS-side loadImage() for .svg files.
    ///
    /// This helper exists because SVGParser.ImportSVG returns a SceneInfo struct
    /// that cannot survive the JS interop boundary (struct serialization loses
    /// complex reference-type properties). By doing the full pipeline in C#,
    /// only the final VectorImage (a ScriptableObject) crosses the boundary.
    ///
    /// Requires com.unity.modules.vectorgraphics, which some Unity-engine forks
    /// (e.g. Tuanjie) do not ship; absent that module, this type does not exist
    /// and loadImage() for .svg fails at the JS/C# boundary instead.
    /// </summary>
    public static class SVGUtils {
        public static VectorImage LoadFromString(string svgContent) {
            using var reader = new StringReader(svgContent);
            var sceneInfo = SVGParser.ImportSVG(reader);
            return VectorUtils.BuildVectorImage(sceneInfo);
        }
    }
}
#endif
