# Default Minecraft profile thumbnails

Steve.png and Alex.png are the default skin textures already distributed with the original
launcher under `PCL.Desktop/Assets/Legacy/Skins`. Only these two image assets are migrated;
no legacy UI code is referenced. Minecraft assets belong to Mojang/Microsoft.

The backend draws the face region (8,8)-(16,16) and the hat layer (40,8)-(48,16)
with nearest-neighbour sampling. These are local placeholders, not downloaded account skins.
