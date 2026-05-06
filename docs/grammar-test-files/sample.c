#include <stdio.h>

/* C grammar sample */
static int add(int left, int right)
{
    return left + right;
}

int main(void)
{
    printf("sum=%d\n", add(2, 3));
    return 0;
}
