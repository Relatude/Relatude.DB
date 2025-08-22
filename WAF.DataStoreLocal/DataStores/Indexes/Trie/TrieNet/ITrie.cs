// This code is distributed under MIT license. Copyright (c) 2013 George Mamaladze
// See license.txt or http://opensource.org/licenses/mit-license.php



// Original source code:
// https://github.com/gmamaladze/trienet


namespace WAF.DataStores.Indexes.Trie.TrieNet; 
/// <summary>
/// Interface to be implemented by a data structure 
/// which allows adding values <see cref="TValue"/> associated with <b>string</b> keys.
/// The interface allows retrieveal of multiple values 
/// </summary>
/// <typeparam name="TValue"></typeparam>
public interface ITrie<TValue> {
    IEnumerable<TValue> Retrieve(string query);
    void Add(string key, TValue value);
}