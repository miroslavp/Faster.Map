# Faster.Map - A collection of Robin-hood hashmaps (FastMap, DenseMapSIMD and DenseMap)

The goal of Faster is to provide a collection of incredible fast hashmaps that integrates into the .net framework.
   
## About
Faster.Map is a collection of hashmaps with minimal memory overhead and incredibly fast runtime speed. See benchmarks, or try it out yourself. Faster evolved from the fact that C# dictionaries in targetframework 4.0 are terribly slow. So i decided to create my own robinhood hashmap, turns out that this hashmap even performs better than the current dictionary written in .net7.
## Get Started
1. Install nuget package Faster.Map to your project.
```
dotnet add package Faster.Map
```
## How to use
Faster.Map provides 3 unique hashmaps:
1. FastMap<Tkey, TValue> is a hashmap which has incredible performance, will only work with numerical keys. Keys need to be unique. Won`t handle hashcollisions

2. DenseMap<Tkey, TValue> is a hashmap which can be used as a replacement to IDicionary. Default loadfactor is 0.5

3. DenseMapSIMD<Tkey, TValue> is a next level hashmap using simd intructions.
   The default loadfactor is 0.9. This allows us to store 15% more entries than a dictionary while maintaining incredible speed.
   This hashmap is fast and i mean mindblowing fast. DenseMapSIMD wont cache hashcodes, using types like strings actually need a wrapper that caches the hashcode. Hence using a slow hash fuction will result in a slow hashmap.

 ## Examples    
  ### Default Example
```C#
private FastMap<uint, uint> _map = new FastMap<uint, uint>(16);     
  _map.Emplace(1, 50); 
  _map.Remove(1);
  _map.Get(1, out var result);
  _map.Update(1, 51); 
 var result = _map[1];    
``` 
  ### DenseMap Example
```C#
private DenseMap<uint, uint> _map = new DenseMap<uint, uint>(16);
 _map.Emplace(1, 50); 
 _map.Remove(1);
 _map.Get(1, out var result);
 _map.Update(1, 51);
 ``` 
 
 ### DenseMapSIMD
``` C#
private DenseMapSIMD<uint, uint> _map = new DenseMapSIMD<uint, uint>(16);
 _map.Emplace(1, 50); 
 _map.Remove(1);
 _map.Get(1, out var result);
 _map.Update(1, 51);
``` 

## Benchmark

### Retrieving a million random generated keys
|         Method |      Mean |     Error |    StdDev |
|--------------- |----------:|----------:|----------:|
| SlimDictionary | 14.041 ms | 0.1952 ms | 0.1731 ms |
|     Dictionary | 16.712 ms | 0.3339 ms | 0.8000 ms |
|   DenseMapSIMD |  8.151 ms | 0.1607 ms | 0.1651 ms |
|       DenseMap | 16.126 ms | 0.1976 ms | 0.1848 ms |
|        FastMap |  9.577 ms | 0.1660 ms | 0.1386 ms |

### Adding a million keys
|         Method |     Mean |    Error |   StdDev |
|--------------- |---------:|---------:|---------:|
| DictionarySlim | 25.09 ms | 0.313 ms | 0.292 ms |
| Dictionary     | 17.05 ms | 0.327 ms | 0.638 ms |
| DenseMapSIMD   | 16.06 ms | 1.083 ms | 3.194 ms |
| FastMap	     | 29.38 ms | 0.603 ms | 1.298 ms |
| DenseMap       | 34.86 ms | 0.768 ms | 0.914 ms |

### Upsdating a million keys
|         Method |     Mean |    Error |   StdDev |
|--------------- |---------:|---------:|---------:|
| SlimDictionary | 14.624 ms | 0.2158 ms | 0.1913 ms |
|   Dictionary   | 19.102 ms | 0.3739 ms | 0.7023 ms |
| DenseMapSIMD   | 13.136 ms | 0.2614 ms | 0.2445 ms |
|      FastMap   |  9.953 ms | 0.1157 ms | 0.1082 ms |
|     DenseMap   | 15.793 ms | 0.1122 ms | 0.0995 ms |

### Removing a million keys
|         Method |      Mean |     Error |    StdDev |
|--------------- |----------:|----------:|----------:|
| SlimDictionary | 18.63 ms | 0.632 ms | 1.853 ms | 1
|     Dictionary | 26.12 ms | 0.531 ms | 1.489 ms | 
|        FastMap | 17.87 ms | 0.357 ms | 0.792 ms | 
|   DenseMapSIMD | 18.77 ms | 0.357 ms | 0.334 ms | 
|       DenseMap | 25.13 ms | 0.447 ms | 0.373 ms | 

