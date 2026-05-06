#include <iostream>
#include <vector>

// C++ grammar sample
class Counter {
public:
    explicit Counter(int start) : value(start) {}
    int next() { return ++value; }

private:
    int value;
};

int main()
{
    Counter counter{41};
    std::cout << "next=" << counter.next() << '\n';
}
