// Runtime test inputs are constructed from deterministic exporter output snapshots.
export const fixtureJson: Record<string, any> = {
  "metadata": {
    "version": 4.7,
    "type": "Object",
    "generator": "Object3D.toJSON"
  },
  "geometries": [
    {
      "uuid": "a481f7fc-af35-71f8-5a5a-8b8b9c9cdede",
      "type": "PlaneGeometry",
      "width": 1,
      "height": 1,
      "widthSegments": 1,
      "heightSegments": 1
    }
  ],
  "materials": [
    {
      "uuid": "b02c2695-e41b-cf31-5a5a-8b8b9c9cdede",
      "type": "MeshBasicMaterial",
      "color": 16777215,
      "envMapRotation": [
        0,
        0,
        0,
        "XYZ"
      ],
      "reflectivity": 1,
      "refractionRatio": 0.98,
      "side": 2,
      "opacity": 0.72,
      "transparent": true,
      "blendColor": 0,
      "depthWrite": false
    }
  ],
  "object": {
    "uuid": "a6b1300f-a13a-e543-5a5a-8b8b9c9cdede",
    "type": "Group",
    "name": "water-impact",
    "layers": 1,
    "matrix": [
      1,
      0,
      0,
      0,
      0,
      1,
      0,
      0,
      0,
      0,
      1,
      0,
      0,
      0,
      0,
      1
    ],
    "up": [
      0,
      1,
      0
    ],
    "children": [
      {
        "uuid": "a5b12e7c-1b4f-1ad8-5a5a-8b8b9c9cdede",
        "type": "ParticleEmitter",
        "name": "water-impact",
        "layers": 1,
        "matrix": [
          1,
          0,
          0,
          0,
          0,
          1,
          0,
          0,
          0,
          0,
          1,
          0,
          0,
          0,
          0,
          1
        ],
        "up": [
          0,
          1,
          0
        ],
        "ps": {
          "version": "3.0",
          "autoDestroy": false,
          "looping": false,
          "prewarm": false,
          "duration": 0.08,
          "shape": {
            "type": "sphere",
            "radius": 0.04,
            "arc": 6.283185307179586,
            "thickness": 0.2,
            "mode": 0,
            "spread": 0,
            "speed": {
              "type": "ConstantValue",
              "value": 1
            }
          },
          "startLife": {
            "type": "IntervalValue",
            "a": 0.28,
            "b": 0.52
          },
          "startSpeed": {
            "type": "IntervalValue",
            "a": 1.1,
            "b": 3.2
          },
          "startRotation": {
            "type": "ConstantValue",
            "value": 0
          },
          "startSize": {
            "type": "IntervalValue",
            "a": 0.025,
            "b": 0.068
          },
          "startColor": {
            "type": "ConstantColor",
            "color": {
              "r": 0.2,
              "g": 0.72,
              "b": 0.95,
              "a": 0.72
            }
          },
          "emissionOverTime": {
            "type": "ConstantValue",
            "value": 0
          },
          "emissionOverDistance": {
            "type": "ConstantValue",
            "value": 0
          },
          "emissionBursts": [
            {
              "time": 0,
              "count": {
                "type": "ConstantValue",
                "value": 18
              },
              "probability": 1,
              "interval": 0.1,
              "cycle": 1
            }
          ],
          "onlyUsedByOther": false,
          "instancingGeometry": "a481f7fc-af35-71f8-5a5a-8b8b9c9cdede",
          "renderOrder": 0,
          "renderMode": 0,
          "rendererEmitterSettings": {},
          "material": "b02c2695-e41b-cf31-5a5a-8b8b9c9cdede",
          "layers": 1,
          "startTileIndex": {
            "type": "ConstantValue",
            "value": 0
          },
          "uTileCount": 1,
          "vTileCount": 1,
          "blendTiles": false,
          "softParticles": false,
          "softFarFade": 1,
          "softNearFade": 0,
          "behaviors": [
            {
              "type": "ForceOverLife",
              "x": {
                "type": "ConstantValue",
                "value": 0
              },
              "y": {
                "type": "ConstantValue",
                "value": -4.8
              },
              "z": {
                "type": "ConstantValue",
                "value": 0
              }
            },
            {
              "type": "SizeOverLife",
              "size": {
                "type": "PiecewiseBezier",
                "functions": [
                  {
                    "function": {
                      "p0": 1,
                      "p1": 1,
                      "p2": 0.05,
                      "p3": 0.05
                    },
                    "start": 0
                  }
                ]
              }
            },
            {
              "type": "ColorOverLife",
              "color": {
                "type": "Gradient",
                "color": {
                  "type": "CLinearFunction",
                  "subType": "Color",
                  "keys": [
                    {
                      "value": {
                        "r": 0.48,
                        "g": 0.92,
                        "b": 1
                      },
                      "pos": 0
                    },
                    {
                      "value": {
                        "r": 0.48,
                        "g": 0.92,
                        "b": 1
                      },
                      "pos": 1
                    }
                  ]
                },
                "alpha": {
                  "type": "CLinearFunction",
                  "subType": "Number",
                  "keys": [
                    {
                      "value": 1,
                      "pos": 0
                    },
                    {
                      "value": 0,
                      "pos": 1
                    }
                  ]
                }
              }
            }
          ],
          "worldSpace": true
        }
      }
    ]
  }
};

export const subemitterFixtureJson: Record<string, any> = {
  "metadata": {
    "version": 4.7,
    "type": "Object",
    "generator": "UnityParticleQuarksExporter"
  },
  "geometries": [
    {
      "uuid": "3e664f81-a340-48ad-5a5a-8b8b9c9cdede",
      "type": "PlaneGeometry",
      "width": 1,
      "height": 1,
      "widthSegments": 1,
      "heightSegments": 1
    },
    {
      "uuid": "3d664dee-1d54-7e42-5a5a-8b8b9c9cdede",
      "type": "PlaneGeometry",
      "width": 1,
      "height": 1,
      "widthSegments": 1,
      "heightSegments": 1
    }
  ],
  "materials": [
    {
      "uuid": "49e37190-447e-52dc-5a5a-8b8b9c9cdede",
      "type": "MeshBasicMaterial",
      "color": 16777215,
      "envMapRotation": [
        0,
        0,
        0,
        "XYZ"
      ],
      "reflectivity": 1,
      "refractionRatio": 0.98,
      "blending": 2,
      "side": 2,
      "transparent": true,
      "blendColor": 0,
      "depthWrite": false
    },
    {
      "uuid": "4ae37323-ca6a-1d47-5a5a-8b8b9c9cdede",
      "type": "MeshBasicMaterial",
      "color": 16777215,
      "envMapRotation": [
        0,
        0,
        0,
        "XYZ"
      ],
      "reflectivity": 1,
      "refractionRatio": 0.98,
      "blending": 2,
      "side": 2,
      "transparent": true,
      "blendColor": 0,
      "depthWrite": false
    }
  ],
  "object": {
    "uuid": "b1dc6fde-5e1b-3f92-5a5a-8b8b9c9cdede",
    "type": "Group",
    "name": "subemitter-burst",
    "layers": 1,
    "matrix": [
      1,
      0,
      0,
      0,
      0,
      1,
      0,
      0,
      0,
      0,
      1,
      0,
      0,
      0,
      0,
      1
    ],
    "up": [
      0,
      1,
      0
    ],
    "children": [
      {
        "uuid": "b2dc7171-e407-09fd-5a5a-8b8b9c9cdede",
        "type": "ParticleEmitter",
        "name": "subemitter-parent",
        "userData": {
          "unityParticleQuarks": {
            "schemaVersion": "unity_particle_quarks_exporter.user_data.v1",
            "subEmitterInheritance": [
              {
                "index": 0,
                "subParticleSystem": "afdc6cb8-5243-aabc-5a5a-8b8b9c9cdede",
                "mode": 1,
                "inheritColor": true,
                "inheritSize": true,
                "inheritRotation": true,
                "inheritLifetime": true,
                "inheritDuration": false
              }
            ]
          }
        },
        "layers": 1,
        "matrix": [
          1,
          0,
          0,
          0,
          0,
          1,
          0,
          0,
          0,
          0,
          1,
          0,
          0,
          0,
          0,
          1
        ],
        "up": [
          0,
          1,
          0
        ],
        "ps": {
          "version": "3.0",
          "autoDestroy": false,
          "looping": true,
          "prewarm": false,
          "duration": 0.55,
          "shape": {
            "type": "point"
          },
          "startLife": {
            "type": "ConstantValue",
            "value": 0.38
          },
          "startSpeed": {
            "type": "IntervalValue",
            "a": 0.35,
            "b": 0.75
          },
          "startRotation": {
            "type": "ConstantValue",
            "value": 0
          },
          "startSize": {
            "type": "ConstantValue",
            "value": 0.22
          },
          "startColor": {
            "type": "ConstantColor",
            "color": {
              "r": 1,
              "g": 0.72,
              "b": 0.2,
              "a": 1
            }
          },
          "emissionOverTime": {
            "type": "ConstantValue",
            "value": 0
          },
          "emissionOverDistance": {
            "type": "ConstantValue",
            "value": 0
          },
          "emissionBursts": [
            {
              "time": 0,
              "count": {
                "type": "ConstantValue",
                "value": 8
              },
              "probability": 1,
              "interval": 0.1,
              "cycle": 1
            }
          ],
          "onlyUsedByOther": false,
          "instancingGeometry": "3e664f81-a340-48ad-5a5a-8b8b9c9cdede",
          "renderOrder": 0,
          "renderMode": 0,
          "rendererEmitterSettings": {},
          "material": "49e37190-447e-52dc-5a5a-8b8b9c9cdede",
          "layers": 1,
          "startTileIndex": {
            "type": "ConstantValue",
            "value": 0
          },
          "uTileCount": 1,
          "vTileCount": 1,
          "blendTiles": false,
          "softParticles": false,
          "softFarFade": 1,
          "softNearFade": 0,
          "behaviors": [
            {
              "type": "EmitSubParticleSystem",
              "subParticleSystem": "afdc6cb8-5243-aabc-5a5a-8b8b9c9cdede",
              "useVelocityAsBasis": false,
              "mode": 1,
              "emitProbability": 1
            }
          ],
          "worldSpace": true
        }
      },
      {
        "uuid": "afdc6cb8-5243-aabc-5a5a-8b8b9c9cdede",
        "type": "ParticleEmitter",
        "name": "subemitter-child",
        "userData": {
          "unityParticleQuarks": {
            "schemaVersion": "unity_particle_quarks_exporter.user_data.v1",
            "subEmitterInheritance": []
          }
        },
        "layers": 1,
        "matrix": [
          1,
          0,
          0,
          0,
          0,
          1,
          0,
          0,
          0,
          0,
          1,
          0,
          0,
          0,
          0,
          1
        ],
        "up": [
          0,
          1,
          0
        ],
        "ps": {
          "version": "3.0",
          "autoDestroy": false,
          "looping": false,
          "prewarm": false,
          "duration": 0.18,
          "shape": {
            "type": "sphere",
            "radius": 0.02,
            "arc": 6.283185307179586,
            "thickness": 1,
            "mode": 0,
            "spread": 0,
            "speed": {
              "type": "ConstantValue",
              "value": 1
            }
          },
          "startLife": {
            "type": "IntervalValue",
            "a": 0.4,
            "b": 0.72
          },
          "startSpeed": {
            "type": "IntervalValue",
            "a": 0.6,
            "b": 1.8
          },
          "startRotation": {
            "type": "ConstantValue",
            "value": 0
          },
          "startSize": {
            "type": "ConstantValue",
            "value": 0.32
          },
          "startColor": {
            "type": "ConstantColor",
            "color": {
              "r": 1,
              "g": 0.3,
              "b": 0.08,
              "a": 0.9
            }
          },
          "emissionOverTime": {
            "type": "ConstantValue",
            "value": 0
          },
          "emissionOverDistance": {
            "type": "ConstantValue",
            "value": 0
          },
          "emissionBursts": [
            {
              "time": 0,
              "count": {
                "type": "ConstantValue",
                "value": 10
              },
              "probability": 1,
              "interval": 0.1,
              "cycle": 1
            }
          ],
          "onlyUsedByOther": true,
          "instancingGeometry": "3d664dee-1d54-7e42-5a5a-8b8b9c9cdede",
          "renderOrder": 0,
          "renderMode": 0,
          "rendererEmitterSettings": {},
          "material": "4ae37323-ca6a-1d47-5a5a-8b8b9c9cdede",
          "layers": 1,
          "startTileIndex": {
            "type": "ConstantValue",
            "value": 0
          },
          "uTileCount": 1,
          "vTileCount": 1,
          "blendTiles": false,
          "softParticles": false,
          "softFarFade": 1,
          "softNearFade": 0,
          "behaviors": [
            {
              "type": "SizeOverLife",
              "size": {
                "type": "PiecewiseBezier",
                "functions": [
                  {
                    "function": {
                      "p0": 1,
                      "p1": 1,
                      "p2": 0,
                      "p3": 0
                    },
                    "start": 0
                  }
                ]
              }
            }
          ],
          "worldSpace": true
        }
      }
    ]
  }
};
