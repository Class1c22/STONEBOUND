using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Читає файл MagicaVoxel (.vox): розмір, список вокселів і палітру. Береться перша модель у файлі.
// Щоб Unity побачила файл, перейменуй його з house.vox на house.vox.bytes
public static class VoxLoader
{
    public struct Voxel
    {
        public byte x, y, z, color;
    }

    public class Model
    {
        public Vector3Int size;
        public List<Voxel> voxels = new List<Voxel>();
        public Color32[] palette;
    }

    public static Model Load(byte[] bytes)
    {
        var model = new Model();
        using (var ms = new MemoryStream(bytes))
        using (var br = new BinaryReader(ms))
        {
            string magic = new string(br.ReadChars(4));
            if (magic != "VOX ")
            {
                Debug.LogError("Це не .vox файл");
                return null;
            }
            br.ReadInt32();   // версія

            bool haveModel = false;
            while (ms.Position + 12 <= ms.Length)
            {
                string id = new string(br.ReadChars(4));
                int content = br.ReadInt32();
                br.ReadInt32();   // розмір дочірніх блоків (читаємо їх підряд, тому не потрібен)
                long next = ms.Position + content;

                switch (id)
                {
                    case "SIZE":
                        if (!haveModel)
                        {
                            int sx = br.ReadInt32();
                            int sy = br.ReadInt32();
                            int sz = br.ReadInt32();
                            model.size = new Vector3Int(sx, sy, sz);
                        }
                        break;

                    case "XYZI":
                        if (!haveModel)
                        {
                            int n = br.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                var v = new Voxel();
                                v.x = br.ReadByte();
                                v.y = br.ReadByte();
                                v.z = br.ReadByte();
                                v.color = br.ReadByte();
                                model.voxels.Add(v);
                            }
                            haveModel = true;
                        }
                        break;

                    case "RGBA":
                        model.palette = new Color32[256];
                        for (int i = 0; i < 256; i++)
                        {
                            byte r = br.ReadByte(), g = br.ReadByte(), b = br.ReadByte(), a = br.ReadByte();
                            model.palette[i] = new Color32(r, g, b, a);
                        }
                        break;
                }
                ms.Position = next;
            }
        }

        if (model.palette == null)
        {
            model.palette = new Color32[256];
            for (int i = 0; i < 256; i++) model.palette[i] = new Color32(200, 200, 200, 255);
        }
        return model;
    }
}
